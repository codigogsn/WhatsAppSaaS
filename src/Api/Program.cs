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

    app.UseGlobalExceptionHandling();
    app.UseRequestLogging();
    app.UseRateLimiter();

    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapControllers();
    app.MapHealthChecks("/health");

    if (!EF.IsDesignTime)
    {
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
        var existing = db.Businesses.FirstOrDefault(b => b.PhoneNumberId == phoneNumberId);
        if (existing is not null)
        {
            var changed = false;
            if (!string.IsNullOrWhiteSpace(accessToken) && existing.AccessToken != accessToken)
            {
                existing.AccessToken = accessToken;
                changed = true;
            }
            if (!string.IsNullOrWhiteSpace(adminKey) && existing.AdminKey != adminKey)
            {
                existing.AdminKey = adminKey;
                changed = true;
            }
            if (!existing.IsActive)
            {
                existing.IsActive = true;
                changed = true;
            }

            if (changed)
            {
                db.SaveChanges();
                Log.Information("SEED: updated existing business PhoneNumberId={PhoneNumberId}", phoneNumberId);
            }
            else
            {
                Log.Information("SEED: business already exists PhoneNumberId={PhoneNumberId} IsActive={IsActive}",
                    phoneNumberId, existing.IsActive);
            }
            return;
        }

        db.Businesses.Add(new WhatsAppSaaS.Domain.Entities.Business
        {
            Name = businessName,
            PhoneNumberId = phoneNumberId,
            AccessToken = accessToken,
            AdminKey = adminKey,
            IsActive = true
        });
        db.SaveChanges();
        Log.Information("SEED: created business PhoneNumberId={PhoneNumberId} Name={Name}", phoneNumberId, businessName);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "SEED: failed to seed business for PhoneNumberId={PhoneNumberId}", phoneNumberId);
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
