using System;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using WhatsAppSaaS.Api.Extensions;
using WhatsAppSaaS.Api.Services;
using WhatsAppSaaS.Application.Validators;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Infrastructure.Extensions;
using WhatsAppSaaS.Infrastructure.Persistence;

// ──────────────────────────────────────────
// Bootstrap Serilog early for startup logging
// ──────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateLogger();

try
{
    Log.Information("Starting WhatsApp SaaS API");

    // Npgsql 6+ maps DateTime to 'timestamptz' by default.
    // Our legacy schema uses 'timestamp' (without tz). Enable compat to prevent exceptions.
    AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

    var builder = WebApplication.CreateBuilder(args);

    if (builder.Environment.IsDevelopment())
    {
        builder.WebHost.UseUrls("http://127.0.0.1:5070");
    }

    // ────────────────────────────────────────
    // Serilog
    // ────────────────────────────────────────
    builder.Services.AddSerilog(configuration => configuration
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "WhatsAppSaaS")
        .WriteTo.Console()
        .WriteTo.File(
            path: "logs/whatsapp-saas-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30));

    // ────────────────────────────────────────
    // Controllers
    // ────────────────────────────────────────
    builder.Services
        .AddControllers()
        .AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
            o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddValidatorsFromAssemblyContaining<WebhookPayloadValidator>();

    // ────────────────────────────────────────
    // DbContext
    // ────────────────────────────────────────
    builder.Services.AddDbContext<AppDbContext>(options =>
    {
        var env = builder.Environment.EnvironmentName;

        var databaseUrl = builder.Configuration["DATABASE_URL"]
                          ?? Environment.GetEnvironmentVariable("DATABASE_URL");

        if (!string.IsNullOrWhiteSpace(databaseUrl))
        {
            var connString = ConvertDatabaseUrlToNpgsql(databaseUrl);
            options.UseNpgsql(connString);
            return;
        }

        if (string.Equals(env, "Production", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("DATABASE_URL is missing in Production.");
        }

        var sqlitePath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "app.db"));
        options.UseSqlite($"Data Source={sqlitePath}");
    });

    builder.Services.AddScoped<Api.Services.IBusinessResolver, Api.Services.BusinessResolver>();
    builder.Services.AddApplicationServices();
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddHealthChecks();
    builder.Services.AddHostedService<WhatsAppSaaS.Api.Services.ConversationCleanupService>();

    // Async webhook processing queue
    builder.Services.AddSingleton<WhatsAppSaaS.Application.Interfaces.IMessageQueue,
        WhatsAppSaaS.Infrastructure.Messaging.InMemoryMessageQueue>();
    builder.Services.AddHostedService<WhatsAppSaaS.Api.Workers.WebhookProcessingWorker>();

    // Background job processing
    builder.Services.AddScoped<WhatsAppSaaS.Application.Interfaces.IBackgroundJobService,
        WhatsAppSaaS.Infrastructure.Services.BackgroundJobService>();
    builder.Services.AddHostedService<WhatsAppSaaS.Api.Workers.BackgroundJobWorker>();
    builder.Services.AddHostedService<WhatsAppSaaS.Api.Workers.CleanupJobScheduler>();
    builder.Services.AddHostedService<WhatsAppSaaS.Api.Workers.CheckoutReminderWorker>();

    // ────────────────────────────────────────
    // JWT Authentication
    // ────────────────────────────────────────
    var jwtSecret = builder.Configuration["Jwt:Secret"]
                    ?? Environment.GetEnvironmentVariable("JWT_SECRET")
                    ?? "";
    if (!string.IsNullOrWhiteSpace(jwtSecret))
    {
        var jwtService = new JwtService(builder.Configuration);
        builder.Services.AddSingleton(jwtService);

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwtService.Issuer,
                    ValidAudience = jwtService.Issuer,
                    IssuerSigningKey = jwtService.GetSigningKey()
                };
            });
    }
    else
    {
        // Fallback: register JwtService that will fail at login time, not at startup
        builder.Services.AddSingleton<JwtService>();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();
    }
    builder.Services.AddAuthorization();

    // ────────────────────────────────────────
    // Rate limiting
    // ────────────────────────────────────────
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = 429;

        // Webhook: 60 requests per minute per IP
        options.AddPolicy("webhook", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromMinutes(1)
                }));

        // Admin: 30 requests per minute per IP
        options.AddPolicy("admin", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 30,
                    Window = TimeSpan.FromMinutes(1)
                }));
    });

    var app = builder.Build();

    // ────────────────────────────────────────
    // DEBUG: DB info endpoint
    // ────────────────────────────────────────
    app.MapGet("/debug/db", (IHostEnvironment env, AppDbContext db) =>
    {
        var conn = db.Database.GetDbConnection();
        var expected = Path.GetFullPath(Path.Combine(env.ContentRootPath, "app.db"));

        string? sqliteDataSource = null;
        try
        {
            if (conn is Microsoft.Data.Sqlite.SqliteConnection sqliteConn)
                sqliteDataSource = sqliteConn.DataSource;
        }
        catch { }

        return Results.Ok(new
        {
            env = env.EnvironmentName,
            provider = db.Database.ProviderName,
            connectionString = conn.ConnectionString,
            sqliteDataSource,
            contentRoot = env.ContentRootPath,
            cwd = Directory.GetCurrentDirectory(),
            expectedAppDb = expected,
            expectedAppDbExists = File.Exists(expected)
        });
    });

    // ────────────────────────────────────────
    // AUTO-MIGRATE (both SQLite and Postgres)
    // ────────────────────────────────────────
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var isNpgsql = db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

        if (isNpgsql)
        {
            ApplyMigrationsWithAdvisoryLock(db);
        }
        else
        {
            try
            {
                Log.Information("MIGRATE START (SQLite)");
                db.Database.Migrate();
                Log.Information("MIGRATE OK (SQLite)");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Failed applying EF migrations on startup.");
                throw;
            }
        }
    }

    // ────────────────────────────────────────
    // Seed default business from env vars (idempotent)
    // ────────────────────────────────────────
    SeedDefaultBusiness(app.Services);
    SeedZelleConfig(app.Services);

    app.UseGlobalExceptionHandling();
    app.UseRequestLogging();
    app.UseRateLimiter();

    // Clean URL routes → static files (preserves backward compat with .html)
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value;
        if (path is "/dashboard" or "/dashboard/")
            context.Request.Path = "/index.html";
        else if (path is "/menu" or "/menu/")
            context.Request.Path = "/menu.html";
        await next();
    });

    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();
    app.MapHealthChecks("/health");

    if (!EF.IsDesignTime)
    {
        // Best-effort: fetch today's BCV rate on startup so it's available immediately
        try
        {
            using var startupScope = app.Services.CreateScope();
            var bcvService = startupScope.ServiceProvider.GetRequiredService<IBcvRateService>();
            var rate = await bcvService.FetchAndPersistTodayAsync();
            if (rate is not null)
                Log.Information("STARTUP BCV rate loaded — USD={Usd} EUR={Eur} date={Date}",
                    rate.UsdRate, rate.EurRate, rate.RateDate.ToString("yyyy-MM-dd"));
            else
                Log.Warning("STARTUP BCV rate fetch returned null — will retry via background job");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "STARTUP BCV rate fetch failed — will retry via background job");
        }

        app.Run();
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// ──────────────────────────────────────────
// Seed default business from environment variables
// ──────────────────────────────────────────
static void SeedDefaultBusiness(IServiceProvider services)
{
    var phoneNumberId = Environment.GetEnvironmentVariable("WHATSAPP_PHONE_NUMBER_ID");
    if (string.IsNullOrWhiteSpace(phoneNumberId))
    {
        Log.Information("SEED: WHATSAPP_PHONE_NUMBER_ID not set, skipping business seed");
        return;
    }

    var accessToken = Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN") ?? "";
    var adminKey = Environment.GetEnvironmentVariable("WHATSAPP_ADMIN_KEY") ?? "";
    var businessName = Environment.GetEnvironmentVariable("WHATSAPP_BUSINESS_NAME") ?? "Default Business";

    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    try
    {
        // Raw SQL: avoids EF Guid cast crash on legacy text Id rows like "biz_demo_001"
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();

        // Count legacy rows with non-uuid Ids for diagnostics (PostgreSQL only)
        var isPostgres = conn.GetType().Name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        if (isPostgres)
        {
            try
            {
                using var diagCmd = conn.CreateCommand();
                diagCmd.CommandText = """
                    SELECT COUNT(*) FROM "Businesses"
                    WHERE CAST("Id" AS TEXT) !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                """;
                var legacyCount = Convert.ToInt64(diagCmd.ExecuteScalar());
                if (legacyCount > 0)
                    Log.Warning("SEED: found {LegacyCount} business row(s) with non-UUID Id — skipped by EF, safe in raw SQL", legacyCount);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "SEED: legacy Id diagnostic query failed (non-critical)");
            }
        }

        // Check if business already exists for this phoneNumberId
        string? existingId = null;
        string? existingToken = null;
        string? existingAdminKey = null;
        bool existingActive = false;

        using (var readCmd = conn.CreateCommand())
        {
            readCmd.CommandText = """
                SELECT CAST("Id" AS TEXT), "AccessToken", "AdminKey", "IsActive"
                FROM "Businesses"
                WHERE "PhoneNumberId" = @pid
                LIMIT 1
            """;
            var p = readCmd.CreateParameter(); p.ParameterName = "pid"; p.Value = phoneNumberId; readCmd.Parameters.Add(p);

            using var r = readCmd.ExecuteReader();
            if (r.Read())
            {
                existingId = r.IsDBNull(0) ? null : r.GetString(0);
                existingToken = r.IsDBNull(1) ? null : r.GetString(1);
                existingAdminKey = r.IsDBNull(2) ? null : r.GetString(2);
                var rawActive = r["IsActive"];
                existingActive = rawActive switch
                {
                    bool bv => bv, int iv => iv != 0, long lv => lv != 0,
                    string sv => sv is "1" or "true" or "True" or "t", _ => true
                };
            }
        }

        if (existingId is not null)
        {
            // Update existing business with raw SQL
            var sets = new List<string>();
            var updateParams = new List<(string name, object value)>();

            if (!string.IsNullOrWhiteSpace(accessToken) && accessToken != existingToken)
            {
                sets.Add("\"AccessToken\" = @token");
                updateParams.Add(("token", accessToken));
            }
            if (!string.IsNullOrWhiteSpace(adminKey) && adminKey != existingAdminKey)
            {
                sets.Add("\"AdminKey\" = @akey");
                updateParams.Add(("akey", adminKey));
            }
            if (!existingActive)
            {
                sets.Add("\"IsActive\" = true");
            }

            if (sets.Count > 0)
            {
                using var updCmd = conn.CreateCommand();
                updCmd.CommandText = $"""UPDATE "Businesses" SET {string.Join(", ", sets)} WHERE "PhoneNumberId" = @pid""";
                var pp = updCmd.CreateParameter(); pp.ParameterName = "pid"; pp.Value = phoneNumberId; updCmd.Parameters.Add(pp);
                foreach (var (name, value) in updateParams)
                {
                    var up = updCmd.CreateParameter(); up.ParameterName = name; up.Value = value; updCmd.Parameters.Add(up);
                }
                updCmd.ExecuteNonQuery();
                Log.Information("SEED: updated existing business PhoneNumberId={PhoneNumberId} Id={Id}", phoneNumberId, existingId);
            }
            else
            {
                Log.Information("SEED: business already exists PhoneNumberId={PhoneNumberId} Id={Id} IsActive={IsActive}",
                    phoneNumberId, existingId, existingActive);
            }
            return;
        }

        // Insert new business with raw SQL
        var newId = Guid.NewGuid();
        using (var insCmd = conn.CreateCommand())
        {
            insCmd.CommandText = """
                INSERT INTO "Businesses" ("Id", "Name", "PhoneNumberId", "AccessToken", "AdminKey", "IsActive", "CreatedAtUtc")
                VALUES (@id, @name, @pid, @token, @akey, true, @created)
            """;
            var ip1 = insCmd.CreateParameter(); ip1.ParameterName = "id"; ip1.Value = newId; insCmd.Parameters.Add(ip1);
            var ip2 = insCmd.CreateParameter(); ip2.ParameterName = "name"; ip2.Value = businessName; insCmd.Parameters.Add(ip2);
            var ip3 = insCmd.CreateParameter(); ip3.ParameterName = "pid"; ip3.Value = phoneNumberId; insCmd.Parameters.Add(ip3);
            var ip4 = insCmd.CreateParameter(); ip4.ParameterName = "token"; ip4.Value = accessToken; insCmd.Parameters.Add(ip4);
            var ip5 = insCmd.CreateParameter(); ip5.ParameterName = "akey"; ip5.Value = adminKey; insCmd.Parameters.Add(ip5);
            var ip6 = insCmd.CreateParameter(); ip6.ParameterName = "created"; ip6.Value = DateTime.UtcNow; insCmd.Parameters.Add(ip6);
            insCmd.ExecuteNonQuery();
        }
        Log.Information("SEED: created business PhoneNumberId={PhoneNumberId} Id={Id} Name={Name}", phoneNumberId, newId, businessName);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "SEED: failed to seed business for PhoneNumberId={PhoneNumberId}", phoneNumberId);
    }
}

// ──────────────────────────────────────────
// Seed Zelle config for active business (one-time, raw SQL)
// ──────────────────────────────────────────
static void SeedZelleConfig(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    try
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) conn.Open();

        // Only update businesses that have NULL ZelleRecipient (idempotent)
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE "Businesses"
            SET "ZelleRecipient" = @recip, "ZelleInstructions" = @instr
            WHERE CAST("IsActive" AS TEXT) IN ('true','1','t') AND "ZelleRecipient" IS NULL
        """;
        var p1 = cmd.CreateParameter(); p1.ParameterName = "recip"; p1.Value = "insertatuzelle@gmail.com"; cmd.Parameters.Add(p1);
        var p2 = cmd.CreateParameter(); p2.ParameterName = "instr"; p2.Value = "Enviar comprobante por WhatsApp"; cmd.Parameters.Add(p2);
        var rows = cmd.ExecuteNonQuery();
        Log.Information("SEED ZELLE: updated {Rows} business(es) with ZelleRecipient/ZelleInstructions", rows);

        // Verify
        using var verify = conn.CreateCommand();
        verify.CommandText = """
            SELECT "Id", "Name", "ZelleRecipient", "ZelleInstructions"
            FROM "Businesses" WHERE CAST("IsActive" AS TEXT) IN ('true','1','t') LIMIT 5
        """;
        using var r = verify.ExecuteReader();
        while (r.Read())
        {
            Log.Information("SEED ZELLE VERIFY: bizId={Id} name={Name} ZelleRecipient={Recip} ZelleInstructions={Instr}",
                r["Id"], r["Name"],
                r["ZelleRecipient"] is DBNull ? "(null)" : r["ZelleRecipient"],
                r["ZelleInstructions"] is DBNull ? "(null)" : r["ZelleInstructions"]);
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "SEED ZELLE: failed to seed Zelle config");
    }
}

// ──────────────────────────────────────────
// Postgres-safe migration with advisory lock + legacy schema repair
// ──────────────────────────────────────────
static void ApplyMigrationsWithAdvisoryLock(AppDbContext db)
{
    const long lockId = 920_717;

    var conn = db.Database.GetDbConnection();
    conn.Open();
    try
    {
        Log.Information("MIGRATION LOCK WAITING...");
        using (var lockCmd = conn.CreateCommand())
        {
            lockCmd.CommandText = $"SET lock_timeout = '60s'; SELECT pg_advisory_lock({lockId})";
            lockCmd.CommandTimeout = 70;
            lockCmd.ExecuteNonQuery();
        }
        Log.Information("MIGRATION LOCK ACQUIRED");

        try
        {
            // Step 1: Full legacy schema repair
            RepairLegacySchema(conn);

            // Step 2: Normal EF migrations (safe now that schema is repaired)
            var pending = db.Database.GetPendingMigrations().ToList();
            if (pending.Count == 0)
            {
                Log.Information("MIGRATE: no pending migrations (Postgres)");
            }
            else
            {
                Log.Information("MIGRATE: {Count} pending: {List}", pending.Count, string.Join(", ", pending));
                db.Database.Migrate();
                Log.Information("MIGRATE OK (Postgres)");
            }

            // Step 3: Fix SQLite-generated column types (idempotent)
            // SQLite migrations create decimal as TEXT and bool as INTEGER.
            // This converts them to native PostgreSQL types.
            try
            {
                Log.Information("SCHEMA FIX: executing column type conversions...");
                using var fixCmd = conn.CreateCommand();
                fixCmd.CommandText = """
                    DO $$
                    DECLARE
                        fixed int := 0;
                    BEGIN
                        -- Helper: convert text → numeric
                        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='SubtotalAmount' AND data_type='text') THEN
                            ALTER TABLE "Orders" ALTER COLUMN "SubtotalAmount" TYPE numeric USING CASE WHEN btrim("SubtotalAmount")='' THEN NULL ELSE "SubtotalAmount"::numeric END;
                            fixed := fixed + 1;
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='TotalAmount' AND data_type='text') THEN
                            ALTER TABLE "Orders" ALTER COLUMN "TotalAmount" TYPE numeric USING CASE WHEN btrim("TotalAmount")='' THEN NULL ELSE "TotalAmount"::numeric END;
                            fixed := fixed + 1;
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='DeliveryFee' AND data_type='text') THEN
                            ALTER TABLE "Orders" ALTER COLUMN "DeliveryFee" TYPE numeric USING CASE WHEN btrim("DeliveryFee")='' THEN NULL ELSE "DeliveryFee"::numeric END;
                            fixed := fixed + 1;
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='LocationLat' AND data_type='text') THEN
                            ALTER TABLE "Orders" ALTER COLUMN "LocationLat" TYPE numeric USING CASE WHEN btrim("LocationLat")='' THEN NULL ELSE "LocationLat"::numeric END;
                            fixed := fixed + 1;
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='LocationLng' AND data_type='text') THEN
                            ALTER TABLE "Orders" ALTER COLUMN "LocationLng" TYPE numeric USING CASE WHEN btrim("LocationLng")='' THEN NULL ELSE "LocationLng"::numeric END;
                            fixed := fixed + 1;
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='CashTenderedAmount' AND data_type='text') THEN
                            ALTER TABLE "Orders" ALTER COLUMN "CashTenderedAmount" TYPE numeric USING CASE WHEN btrim("CashTenderedAmount")='' THEN NULL ELSE "CashTenderedAmount"::numeric END;
                            fixed := fixed + 1;
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='CashBcvRateUsed' AND data_type='text') THEN
                            ALTER TABLE "Orders" ALTER COLUMN "CashBcvRateUsed" TYPE numeric USING CASE WHEN btrim("CashBcvRateUsed")='' THEN NULL ELSE "CashBcvRateUsed"::numeric END;
                            fixed := fixed + 1;
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='CashChangeAmount' AND data_type='text') THEN
                            ALTER TABLE "Orders" ALTER COLUMN "CashChangeAmount" TYPE numeric USING CASE WHEN btrim("CashChangeAmount")='' THEN NULL ELSE "CashChangeAmount"::numeric END;
                            fixed := fixed + 1;
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='CashChangeAmountBs' AND data_type='text') THEN
                            ALTER TABLE "Orders" ALTER COLUMN "CashChangeAmountBs" TYPE numeric USING CASE WHEN btrim("CashChangeAmountBs")='' THEN NULL ELSE "CashChangeAmountBs"::numeric END;
                            fixed := fixed + 1;
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='CashChangeRequired' AND data_type<>'boolean') THEN
                            ALTER TABLE "Orders" ALTER COLUMN "CashChangeRequired" DROP DEFAULT;
                            ALTER TABLE "Orders" ALTER COLUMN "CashChangeRequired" TYPE boolean USING CASE WHEN "CashChangeRequired"::text IN ('1','true','t') THEN true ELSE false END;
                            ALTER TABLE "Orders" ALTER COLUMN "CashChangeRequired" SET DEFAULT false;
                            fixed := fixed + 1;
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='CashChangeReturned' AND data_type<>'boolean') THEN
                            ALTER TABLE "Orders" ALTER COLUMN "CashChangeReturned" DROP DEFAULT;
                            ALTER TABLE "Orders" ALTER COLUMN "CashChangeReturned" TYPE boolean USING CASE WHEN "CashChangeReturned"::text IN ('1','true','t') THEN true ELSE false END;
                            ALTER TABLE "Orders" ALTER COLUMN "CashChangeReturned" SET DEFAULT false;
                            fixed := fixed + 1;
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='OrderItems' AND column_name='UnitPrice' AND data_type='text') THEN
                            ALTER TABLE "OrderItems" ALTER COLUMN "UnitPrice" TYPE numeric USING CASE WHEN btrim("UnitPrice")='' THEN NULL ELSE "UnitPrice"::numeric END;
                            fixed := fixed + 1;
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='OrderItems' AND column_name='LineTotal' AND data_type='text') THEN
                            ALTER TABLE "OrderItems" ALTER COLUMN "LineTotal" TYPE numeric USING CASE WHEN btrim("LineTotal")='' THEN NULL ELSE "LineTotal"::numeric END;
                            fixed := fixed + 1;
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='MenuItems' AND column_name='Price' AND data_type='text') THEN
                            ALTER TABLE "MenuItems" ALTER COLUMN "Price" TYPE numeric USING CASE WHEN btrim("Price")='' THEN NULL ELSE "Price"::numeric END;
                            fixed := fixed + 1;
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Customers' AND column_name='TotalSpent' AND data_type='text') THEN
                            ALTER TABLE "Customers" ALTER COLUMN "TotalSpent" TYPE numeric USING CASE WHEN btrim("TotalSpent")='' THEN NULL ELSE "TotalSpent"::numeric END;
                            fixed := fixed + 1;
                        END IF;
                        RAISE NOTICE 'Schema fix: % columns converted', fixed;
                    END $$;
                """;
                fixCmd.ExecuteNonQuery();
                Log.Information("SCHEMA FIX: column type conversion complete — verifying...");

                // Verify critical columns are now correct types
                using var verifyCmd = conn.CreateCommand();
                verifyCmd.CommandText = """
                    SELECT table_name, column_name, data_type
                    FROM information_schema.columns
                    WHERE (table_name, column_name) IN (
                        ('Orders','SubtotalAmount'),('Orders','TotalAmount'),('Orders','DeliveryFee'),
                        ('Orders','CashChangeRequired'),('Orders','CashChangeReturned'),
                        ('Orders','CashTenderedAmount'),('Orders','CashChangeAmount'),
                        ('OrderItems','UnitPrice'),('OrderItems','LineTotal'),
                        ('MenuItems','Price'),('Customers','TotalSpent')
                    )
                    ORDER BY table_name, column_name
                """;
                using var vr = verifyCmd.ExecuteReader();
                while (vr.Read())
                {
                    var tbl = vr.GetString(0);
                    var col = vr.GetString(1);
                    var dtype = vr.GetString(2);
                    var ok = dtype is "numeric" or "boolean";
                    if (ok)
                        Log.Information("SCHEMA VERIFY: {Table}.{Column} = {Type} ✓", tbl, col, dtype);
                    else
                        Log.Error("SCHEMA VERIFY: {Table}.{Column} = {Type} ✗ STILL WRONG", tbl, col, dtype);
                }
            }
            catch (Npgsql.PostgresException pgEx)
            {
                Log.Fatal(pgEx, "SCHEMA FIX FAILED (Postgres): SqlState={SqlState} Table={Table} Column={Column} " +
                    "Constraint={Constraint} MessageText={Msg} Detail={Detail} Hint={Hint}",
                    pgEx.SqlState, pgEx.TableName, pgEx.ColumnName,
                    pgEx.ConstraintName, pgEx.MessageText, pgEx.Detail, pgEx.Hint);
                throw; // HARD FAIL — app must not start with broken schema
            }
            catch (Exception schemaEx)
            {
                Log.Fatal(schemaEx, "SCHEMA FIX FAILED: {Type}: {Message}", schemaEx.GetType().Name, schemaEx.Message);
                throw; // HARD FAIL
            }
        }
        finally
        {
            try
            {
                using var unlockCmd = conn.CreateCommand();
                unlockCmd.CommandText = $"SELECT pg_advisory_unlock({lockId})";
                unlockCmd.ExecuteNonQuery();
                Log.Information("MIGRATION LOCK RELEASED");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "MIGRATION LOCK RELEASE failed (non-fatal)");
            }
        }
    }
    catch (Exception ex)
    {
        Log.Fatal(ex, "Failed applying EF migrations on startup.");
        throw;
    }
    finally
    {
        conn.Close();
    }
}

// ──────────────────────────────────────────
// Legacy schema repair: bring a partially-migrated production DB
// in line with the current EF model BEFORE running migrations.
// All statements are idempotent (IF NOT EXISTS / IF EXISTS).
// ──────────────────────────────────────────
static void RepairLegacySchema(System.Data.Common.DbConnection conn)
{
    Log.Information("LEGACY SCHEMA REPAIR START");

    // Ensure __EFMigrationsHistory exists
    ExecSql(conn, """CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" ("MigrationId" varchar(150) NOT NULL PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL)""");

    // Remove falsely-recorded InitV2 if the schema is incomplete
    ExecSql(conn, """
        DO $$ BEGIN
            IF EXISTS (SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260305063008_InitV2')
               AND EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'Orders')
               AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Orders' AND column_name = 'BusinessId')
            THEN
                DELETE FROM "__EFMigrationsHistory"
                WHERE "MigrationId" IN (
                    '20260305063008_InitV2',
                    '20260305161922_AddAnalyticsFields',
                    '20260305170406_AddCompositeIndexBusinessCheckout',
                    '20260305200000_FixBooleanColumnsForPostgres'
                );
                RAISE NOTICE 'Removed falsely-recorded migrations from history';
            END IF;
        END $$;
    """);

    // ── Tables (CREATE IF NOT EXISTS) ──
    ExecSql(conn, """CREATE TABLE IF NOT EXISTS "Businesses" ("Id" uuid NOT NULL PRIMARY KEY, "Name" text NOT NULL, "PhoneNumberId" text NOT NULL, "AccessToken" text NOT NULL, "AdminKey" text NOT NULL, "IsActive" boolean NOT NULL DEFAULT true, "CreatedAtUtc" timestamp NOT NULL DEFAULT now())""");
    ExecSql(conn, """CREATE TABLE IF NOT EXISTS "Customers" ("Id" uuid NOT NULL PRIMARY KEY, "BusinessId" uuid, "PhoneE164" text NOT NULL, "Name" text, "TotalSpent" numeric(12,2) NOT NULL DEFAULT 0, "OrdersCount" integer NOT NULL DEFAULT 0, "FirstSeenAtUtc" timestamp NOT NULL DEFAULT now(), "LastSeenAtUtc" timestamp, "LastPurchaseAtUtc" timestamp)""");
    ExecSql(conn, """CREATE TABLE IF NOT EXISTS "Products" ("Id" uuid NOT NULL PRIMARY KEY, "Name" text NOT NULL, "Price" numeric NOT NULL DEFAULT 0)""");
    ExecSql(conn, """CREATE TABLE IF NOT EXISTS "ConversationStates" ("ConversationId" varchar(256) NOT NULL PRIMARY KEY, "BusinessId" uuid, "UpdatedAtUtc" timestamp NOT NULL DEFAULT now(), "StateJson" text NOT NULL DEFAULT '{}')""");
    ExecSql(conn, """CREATE TABLE IF NOT EXISTS "ProcessedMessages" ("Id" uuid NOT NULL PRIMARY KEY, "ConversationId" varchar(256) NOT NULL, "MessageId" varchar(256) NOT NULL, "CreatedAtUtc" timestamp NOT NULL DEFAULT now())""");
    ExecSql(conn, """CREATE TABLE IF NOT EXISTS "Orders" ("Id" uuid NOT NULL PRIMARY KEY, "From" text NOT NULL, "PhoneNumberId" text NOT NULL, "DeliveryType" text NOT NULL DEFAULT 'pickup', "Status" text NOT NULL DEFAULT 'Pending', "CreatedAtUtc" timestamp NOT NULL DEFAULT now(), "CheckoutFormSent" boolean NOT NULL DEFAULT false, "CheckoutCompleted" boolean NOT NULL DEFAULT false)""");
    ExecSql(conn, """CREATE TABLE IF NOT EXISTS "OrderItems" ("Id" uuid NOT NULL PRIMARY KEY, "OrderId" uuid NOT NULL, "Name" text NOT NULL, "Quantity" integer NOT NULL DEFAULT 1, "UnitPrice" numeric(12,2), "LineTotal" numeric(12,2))""");

    // ── Fix SQLite-style text columns → uuid ──
    // InitV2 migration was generated for SQLite (all Guid columns = TEXT).
    // If it ran against Postgres, every Id/FK column is 'text' instead of 'uuid'.
    // This block detects and fixes that, converting text→uuid for all Guid columns.
    RepairTextToUuid(conn);

    // ── Missing columns on Orders (ADD COLUMN IF NOT EXISTS) ──
    string[] orderColumns =
    [
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "BusinessId" uuid""",
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "CustomerId" uuid""",
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "CustomerName" text""",
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "CustomerIdNumber" text""",
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "CustomerPhone" text""",
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "Address" text""",
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "PaymentMethod" text""",
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "ReceiverName" text""",
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "AdditionalNotes" text""",
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "LocationLat" numeric(9,6)""",
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "LocationLng" numeric(9,6)""",
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "LocationText" text""",
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "CheckoutCompletedAtUtc" timestamp""",
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "LastNotifiedStatus" text""",
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "LastNotifiedAtUtc" timestamp""",
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "SubtotalAmount" numeric(12,2)""",
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "TotalAmount" numeric(12,2)""",
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "AcceptedAtUtc" timestamp""",
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "DeliveredAtUtc" timestamp""",
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "DeliveryFee" numeric(12,2)""",
        """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "PreparingAtUtc" timestamp""",
    ];
    foreach (var sql in orderColumns)
    {
        if (ExecSql(conn, sql))
            Log.Information("LEGACY COLUMN ADDED: {Col}", sql.Replace("""ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS """, "Orders."));
    }

    // ── Missing columns on Businesses (payment + profile) ──
    string[] businessColumns =
    [
        """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "PaymentMobileBank" text""",
        """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "PaymentMobileId" text""",
        """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "PaymentMobilePhone" text""",
        """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "Greeting" text""",
        """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "Schedule" text""",
        """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "Address" text""",
        """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "LogoUrl" text""",
        """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "NotificationPhone" character varying(50)""",
        """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "RestaurantType" character varying(50)""",
    ];
    foreach (var sql in businessColumns)
    {
        if (ExecSql(conn, sql))
            Log.Information("LEGACY COLUMN ADDED: {Col}", sql.Replace("""ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS """, "Businesses."));
    }

    // ── Missing column on Orders (SpecialInstructions) ──
    ExecSql(conn, """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "SpecialInstructions" text""");

    // ── Payment proof columns on Orders ──
    ExecSql(conn, """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "PaymentProofMediaId" text""");
    ExecSql(conn, """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "PaymentProofSubmittedAtUtc" timestamp""");
    ExecSql(conn, """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "PaymentVerifiedAtUtc" timestamp""");
    ExecSql(conn, """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "PaymentVerifiedBy" text""");

    // ── Missing columns on Customers (scale features) ──
    ExecSql(conn, """ALTER TABLE "Customers" ADD COLUMN IF NOT EXISTS "LastDeliveryAddress" text""");

    // ── Menu system tables ──
    ExecSql(conn, """CREATE TABLE IF NOT EXISTS "MenuCategories" ("Id" uuid NOT NULL PRIMARY KEY, "BusinessId" uuid NOT NULL REFERENCES "Businesses"("Id") ON DELETE CASCADE, "Name" text NOT NULL, "SortOrder" integer NOT NULL DEFAULT 0, "IsActive" boolean NOT NULL DEFAULT true, "CreatedAtUtc" timestamp NOT NULL DEFAULT now())""");
    ExecSql(conn, """CREATE TABLE IF NOT EXISTS "MenuItems" ("Id" uuid NOT NULL PRIMARY KEY, "CategoryId" uuid NOT NULL REFERENCES "MenuCategories"("Id") ON DELETE CASCADE, "Name" text NOT NULL, "Price" numeric(12,2) NOT NULL DEFAULT 0, "Description" text, "IsAvailable" boolean NOT NULL DEFAULT true, "SortOrder" integer NOT NULL DEFAULT 0, "CreatedAtUtc" timestamp NOT NULL DEFAULT now())""");
    ExecSql(conn, """CREATE TABLE IF NOT EXISTS "MenuItemAliases" ("Id" uuid NOT NULL PRIMARY KEY, "MenuItemId" uuid NOT NULL REFERENCES "MenuItems"("Id") ON DELETE CASCADE, "Alias" text NOT NULL)""");
    ExecSql(conn, """CREATE INDEX IF NOT EXISTS "IX_MenuCategories_BusinessId" ON "MenuCategories" ("BusinessId")""");
    ExecSql(conn, """CREATE INDEX IF NOT EXISTS "IX_MenuItems_CategoryId" ON "MenuItems" ("CategoryId")""");
    ExecSql(conn, """CREATE INDEX IF NOT EXISTS "IX_MenuItemAliases_MenuItemId" ON "MenuItemAliases" ("MenuItemId")""");

    // ── BusinessUsers table ──
    ExecSql(conn, """CREATE TABLE IF NOT EXISTS "BusinessUsers" ("Id" uuid NOT NULL PRIMARY KEY, "BusinessId" uuid NOT NULL REFERENCES "Businesses"("Id") ON DELETE CASCADE, "Name" text NOT NULL, "Email" text NOT NULL, "PasswordHash" text NOT NULL, "Role" text NOT NULL DEFAULT 'Operator', "IsActive" boolean NOT NULL DEFAULT true, "CreatedAtUtc" timestamp NOT NULL DEFAULT now())""");
    ExecSql(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_BusinessUsers_BusinessId_Email" ON "BusinessUsers" ("BusinessId", "Email")""");

    // ── BackgroundJobs table ──
    ExecSql(conn, """CREATE TABLE IF NOT EXISTS "BackgroundJobs" ("Id" uuid NOT NULL PRIMARY KEY, "JobType" varchar(100) NOT NULL, "PayloadJson" text NOT NULL DEFAULT '{}', "Status" varchar(20) NOT NULL DEFAULT 'Pending', "RetryCount" integer NOT NULL DEFAULT 0, "MaxRetries" integer NOT NULL DEFAULT 3, "LastError" varchar(2000), "ScheduledAtUtc" timestamp NOT NULL DEFAULT now(), "LockedAtUtc" timestamp, "CompletedAtUtc" timestamp, "BusinessId" uuid)""");
    ExecSql(conn, """CREATE INDEX IF NOT EXISTS "IX_BackgroundJobs_Status_ScheduledAtUtc" ON "BackgroundJobs" ("Status", "ScheduledAtUtc")""");

    // ── ExchangeRates table ──
    ExecSql(conn, """CREATE TABLE IF NOT EXISTS "ExchangeRates" ("Id" uuid NOT NULL PRIMARY KEY, "RateDate" timestamp NOT NULL, "UsdRate" numeric(12,2) NOT NULL, "EurRate" numeric(12,2) NOT NULL, "Source" varchar(50) NOT NULL DEFAULT 'bcv', "FetchedAtUtc" timestamp NOT NULL DEFAULT now())""");
    ExecSql(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExchangeRates_RateDate" ON "ExchangeRates" ("RateDate")""");

    // ── CurrencyReference column on Businesses ──
    ExecSql(conn, """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "CurrencyReference" varchar(20)""");
    // Default existing businesses with NULL CurrencyReference to BCV_USD
    ExecSql(conn, """UPDATE "Businesses" SET "CurrencyReference" = 'BCV_USD' WHERE "CurrencyReference" IS NULL""");

    // ── VerticalType column on Businesses ──
    ExecSql(conn, """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "VerticalType" varchar(30) DEFAULT 'restaurant'""");
    ExecSql(conn, """UPDATE "Businesses" SET "VerticalType" = 'restaurant' WHERE "VerticalType" IS NULL""");

    // ── Per-business menu PDF ──
    ExecSql(conn, """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "MenuPdfUrl" varchar(500)""");
    ExecSql(conn, """CREATE TABLE IF NOT EXISTS "MenuPdfs" ("Id" uuid NOT NULL PRIMARY KEY, "BusinessId" uuid NOT NULL REFERENCES "Businesses"("Id") ON DELETE CASCADE, "Data" bytea NOT NULL, "ContentType" varchar(100) NOT NULL DEFAULT 'application/pdf', "UploadedAtUtc" timestamp NOT NULL DEFAULT now())""");
    ExecSql(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_MenuPdfs_BusinessId" ON "MenuPdfs" ("BusinessId")""");

    // ── Boolean column repair ──
    string[] boolRepairs =
    [
        """DO $$ BEGIN IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Businesses' AND column_name='IsActive' AND data_type<>'boolean') THEN ALTER TABLE "Businesses" ALTER COLUMN "IsActive" TYPE boolean USING CASE WHEN "IsActive"=0 THEN false ELSE true END; END IF; END $$""",
        """DO $$ BEGIN IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='CheckoutCompleted' AND data_type<>'boolean') THEN ALTER TABLE "Orders" ALTER COLUMN "CheckoutCompleted" TYPE boolean USING CASE WHEN "CheckoutCompleted"=0 THEN false ELSE true END; END IF; END $$""",
        """DO $$ BEGIN IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='CheckoutFormSent' AND data_type<>'boolean') THEN ALTER TABLE "Orders" ALTER COLUMN "CheckoutFormSent" TYPE boolean USING CASE WHEN "CheckoutFormSent"=0 THEN false ELSE true END; END IF; END $$""",
    ];
    var boolFixed = false;
    foreach (var sql in boolRepairs)
    {
        if (ExecSql(conn, sql)) boolFixed = true;
    }
    Log.Information(boolFixed ? "LEGACY BOOL REPAIR APPLIED" : "LEGACY BOOL REPAIR SKIPPED");

    // ── Indexes (IF NOT EXISTS) ──
    ExecSql(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_Businesses_PhoneNumberId" ON "Businesses" ("PhoneNumberId")""");
    ExecSql(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_Customers_BusinessId_PhoneE164" ON "Customers" ("BusinessId", "PhoneE164")""");
    ExecSql(conn, """CREATE INDEX IF NOT EXISTS "IX_OrderItems_OrderId" ON "OrderItems" ("OrderId")""");
    ExecSql(conn, """CREATE INDEX IF NOT EXISTS "IX_Orders_CustomerId" ON "Orders" ("CustomerId")""");
    ExecSql(conn, """CREATE INDEX IF NOT EXISTS "IX_Orders_BusinessId" ON "Orders" ("BusinessId")""");
    ExecSql(conn, """CREATE INDEX IF NOT EXISTS "IX_Orders_CreatedAtUtc" ON "Orders" ("CreatedAtUtc")""");
    ExecSql(conn, """CREATE INDEX IF NOT EXISTS "IX_Orders_BusinessId_CheckoutCompleted" ON "Orders" ("BusinessId", "CheckoutCompleted")""");
    ExecSql(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ProcessedMessages_ConversationId_MessageId" ON "ProcessedMessages" ("ConversationId", "MessageId")""");

    // ── Foreign keys (check before adding) ──
    AddFkIfMissing(conn, "FK_ProcessedMessages_ConversationStates_ConversationId",
        """ALTER TABLE "ProcessedMessages" ADD CONSTRAINT "FK_ProcessedMessages_ConversationStates_ConversationId" FOREIGN KEY ("ConversationId") REFERENCES "ConversationStates" ("ConversationId") ON DELETE CASCADE""");
    AddFkIfMissing(conn, "FK_Orders_Customers_CustomerId",
        """ALTER TABLE "Orders" ADD CONSTRAINT "FK_Orders_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES "Customers" ("Id") ON DELETE SET NULL""");
    AddFkIfMissing(conn, "FK_OrderItems_Orders_OrderId",
        """ALTER TABLE "OrderItems" ADD CONSTRAINT "FK_OrderItems_Orders_OrderId" FOREIGN KEY ("OrderId") REFERENCES "Orders" ("Id") ON DELETE CASCADE""");

    // ── Mark all known migrations as applied ──
    // After repair, the schema is fully aligned with what all these migrations produce.
    string[] allMigrations =
    [
        "20260305063008_InitV2",
        "20260305161922_AddAnalyticsFields",
        "20260305170406_AddCompositeIndexBusinessCheckout",
        "20260305200000_FixBooleanColumnsForPostgres",
        "20260306182317_AddBusinessPaymentMobileFields",
        "20260306202633_AddSpecialInstructions",
        "20260307035321_AddMenuSystem",
        "20260307044447_AddBusinessProfile",
        "20260307182204_AddCustomerLastDeliveryAddress",
        "20260307184701_AddBusinessNotificationPhone",
        "20260307191229_AddPaymentProofFields",
        "20260307193423_AddBusinessUsers",
        "20260307212018_AddRestaurantType",
        "20260308012829_AddBackgroundJobs",
        "20260309174326_AddExchangeRatesAndCurrencyRef",
        "20260309201857_AddVerticalType",
        "20260315000747_AddMenuPdfUpload",
    ];
    foreach (var mid in allMigrations)
    {
        ExecSql(conn, $"""INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('{mid}', '8.0.11') ON CONFLICT ("MigrationId") DO NOTHING""");
    }

    Log.Information("LEGACY SCHEMA REPAIR DONE");
}

static bool ExecSql(System.Data.Common.DbConnection conn, string sql)
{
    try
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
        return true;
    }
    catch (Exception ex)
    {
        Log.Warning("LEGACY REPAIR SQL warning: {Message} — SQL: {Sql}", ex.Message, sql[..Math.Min(sql.Length, 120)]);
        return false;
    }
}

static void AddFkIfMissing(System.Data.Common.DbConnection conn, string constraintName, string alterSql)
{
    try
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = $"SELECT 1 FROM pg_constraint WHERE conname = '{constraintName}'";
        var exists = checkCmd.ExecuteScalar() is not null;
        if (!exists)
            ExecSql(conn, alterSql);
    }
    catch (Exception ex)
    {
        Log.Warning("LEGACY FK check warning: {Message}", ex.Message);
    }
}

// ──────────────────────────────────────────
// Fix text→uuid columns from SQLite-generated InitV2 migration
// ──────────────────────────────────────────
static void RepairTextToUuid(System.Data.Common.DbConnection conn)
{
    // Check if Businesses.Id is text — if not, nothing to fix
    bool needsFix;
    try
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = """
            SELECT 1 FROM information_schema.columns
            WHERE table_schema='public' AND table_name='Businesses' AND column_name='Id' AND data_type='text'
        """;
        needsFix = checkCmd.ExecuteScalar() is not null;
    }
    catch { return; }

    if (!needsFix)
    {
        Log.Information("TEXT→UUID REPAIR: not needed (columns already uuid)");
        return;
    }

    Log.Warning("TEXT→UUID REPAIR: detected text-typed Guid columns from SQLite migration — converting to uuid");

    // Step 1: Drop ALL foreign key constraints (they reference text columns; will be re-added later)
    try
    {
        using var fkCmd = conn.CreateCommand();
        fkCmd.CommandText = """
            DO $$ DECLARE r RECORD;
            BEGIN
              FOR r IN (
                SELECT tc.constraint_name, tc.table_name
                FROM information_schema.table_constraints tc
                WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = 'public'
              ) LOOP
                EXECUTE format('ALTER TABLE %I DROP CONSTRAINT IF EXISTS %I', r.table_name, r.constraint_name);
              END LOOP;
            END $$;
        """;
        fkCmd.ExecuteNonQuery();
        Log.Information("TEXT→UUID REPAIR: dropped all FK constraints");
    }
    catch (Exception ex)
    {
        Log.Warning("TEXT→UUID REPAIR: FK drop warning: {Msg}", ex.Message);
    }

    // Step 2: Convert all text→uuid columns
    // Each entry: (table, column)
    (string table, string column)[] uuidColumns =
    [
        // Primary keys
        ("Businesses", "Id"),
        ("Customers", "Id"),
        ("Products", "Id"),
        ("ProcessedMessages", "Id"),
        ("Orders", "Id"),
        ("OrderItems", "Id"),
        ("MenuCategories", "Id"),
        ("MenuItems", "Id"),
        ("MenuItemAliases", "Id"),
        ("BusinessUsers", "Id"),
        ("BackgroundJobs", "Id"),
        ("ExchangeRates", "Id"),
        ("MenuPdfs", "Id"),
        // Foreign keys
        ("Customers", "BusinessId"),
        ("ConversationStates", "BusinessId"),
        ("Orders", "BusinessId"),
        ("Orders", "CustomerId"),
        ("OrderItems", "OrderId"),
        ("MenuCategories", "BusinessId"),
        ("MenuItems", "CategoryId"),
        ("MenuItemAliases", "MenuItemId"),
        ("BusinessUsers", "BusinessId"),
        ("BackgroundJobs", "BusinessId"),
        ("MenuPdfs", "BusinessId"),
    ];

    foreach (var (table, column) in uuidColumns)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            // Only alter if column exists AND is text (idempotent)
            cmd.CommandText = $"""
                DO $$ BEGIN
                  IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema='public' AND table_name='{table}' AND column_name='{column}' AND data_type='text'
                  ) THEN
                    ALTER TABLE "{table}" ALTER COLUMN "{column}" TYPE uuid USING "{column}"::uuid;
                    RAISE NOTICE 'Converted %.% text→uuid', '{table}', '{column}';
                  END IF;
                END $$;
            """;
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Warning("TEXT→UUID REPAIR: failed {Table}.{Col}: {Msg}", table, column, ex.Message);
        }
    }

    Log.Information("TEXT→UUID REPAIR: done");
}

// ──────────────────────────────────────────
// Render DATABASE_URL helper
// ──────────────────────────────────────────
static string ConvertDatabaseUrlToNpgsql(string databaseUrl)
{
    var uri = new Uri(databaseUrl);

    var userInfo = uri.UserInfo.Split(':', 2);
    var username = Uri.UnescapeDataString(userInfo[0]);
    var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";

    var host = uri.Host;
    var port = uri.Port > 0 ? uri.Port : 5432;
    var database = uri.AbsolutePath.TrimStart('/');

    return $"Host={host};Port={port};Database={database};Username={username};Password={password};Ssl Mode=Require;Trust Server Certificate=true";
}

namespace WhatsAppSaaS.Api
{
    public partial class Program { }
}
