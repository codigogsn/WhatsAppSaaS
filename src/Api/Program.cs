using System;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using WhatsAppSaaS.Api.Extensions;
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

    // ✅ Forzar puerto local estable
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

        // ✅ SQLite ABSOLUTO: SIEMPRE apunta a src/Api/app.db
        var sqlitePath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "app.db"));
        options.UseSqlite($"Data Source={sqlitePath}");
    });

    builder.Services.AddScoped<Api.Services.IBusinessResolver, Api.Services.BusinessResolver>();
    builder.Services.AddApplicationServices();
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddHealthChecks();
    builder.Services.AddHostedService<WhatsAppSaaS.Api.Services.ConversationCleanupService>();

    var app = builder.Build();

    // ────────────────────────────────────────
    // ✅ DEBUG: ver exactamente qué DB está usando el proceso
    // ────────────────────────────────────────
    app.MapGet("/debug/db", (IHostEnvironment env, AppDbContext db) =>
    {
        var conn = db.Database.GetDbConnection();
        var expected = Path.GetFullPath(Path.Combine(env.ContentRootPath, "app.db"));

        // Intento extra para SQLite: DataSource (si aplica)
        string? sqliteDataSource = null;
        try
        {
            // Requiere paquete Microsoft.Data.Sqlite (ya lo tienes porque usas UseSqlite)
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
    // ✅ AUTO-MIGRATE (both SQLite and Postgres)
    // Uses Postgres advisory lock to prevent concurrent migration crashes.
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

    app.UseDefaultFiles();
    app.UseStaticFiles();

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
            // Update token/key if changed in env vars
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
// Postgres-safe migration with advisory lock
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
            // ── Step 1: Schema repair BEFORE EF migrations ──
            RepairBooleanColumns(conn);

            // ── Step 2: Apply migrations one-by-one ──
            // Do NOT use a single Migrate() call — if one migration fails with 42P07
            // (table already exists from old migrations), we record it and continue
            // so remaining migrations (like FixBooleanColumnsForPostgres) still run.
            var migrator = db.GetInfrastructure().GetRequiredService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            var pending = db.Database.GetPendingMigrations().ToList();

            if (pending.Count == 0)
            {
                Log.Information("MIGRATE: no pending migrations (Postgres)");
            }
            else
            {
                Log.Information("MIGRATE: {Count} pending migrations: {Migrations}", pending.Count, string.Join(", ", pending));
                foreach (var migration in pending)
                {
                    try
                    {
                        Log.Information("MIGRATE START: {Migration}", migration);
                        migrator.Migrate(migration);
                        Log.Information("MIGRATE OK: {Migration}", migration);
                    }
                    catch (Npgsql.PostgresException pgEx) when (pgEx.SqlState == "42P07")
                    {
                        Log.Warning("MIGRATE 42P07: {Migration} — objects already exist, recording as applied. Detail: {Detail}",
                            migration, pgEx.MessageText);
                        RecordMigrationAsApplied(conn, migration);
                    }
                }
            }

            Log.Information("MIGRATE OK (Postgres) — all migrations processed");
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
// Pre-migration schema repair for boolean columns
// ──────────────────────────────────────────
static void RepairBooleanColumns(System.Data.Common.DbConnection conn)
{
    Log.Information("SCHEMA REPAIR START");

    var repaired = false;
    string[] repairs =
    [
        """
        DO $$ BEGIN
            IF EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_name = 'Businesses' AND column_name = 'IsActive' AND data_type <> 'boolean'
            ) THEN
                ALTER TABLE "Businesses" ALTER COLUMN "IsActive" TYPE boolean USING CASE WHEN "IsActive" = 0 THEN false ELSE true END;
                RAISE NOTICE 'Repaired Businesses.IsActive';
            END IF;
        END $$;
        """,
        """
        DO $$ BEGIN
            IF EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_name = 'Orders' AND column_name = 'CheckoutCompleted' AND data_type <> 'boolean'
            ) THEN
                ALTER TABLE "Orders" ALTER COLUMN "CheckoutCompleted" TYPE boolean USING CASE WHEN "CheckoutCompleted" = 0 THEN false ELSE true END;
                RAISE NOTICE 'Repaired Orders.CheckoutCompleted';
            END IF;
        END $$;
        """,
        """
        DO $$ BEGIN
            IF EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_name = 'Orders' AND column_name = 'CheckoutFormSent' AND data_type <> 'boolean'
            ) THEN
                ALTER TABLE "Orders" ALTER COLUMN "CheckoutFormSent" TYPE boolean USING CASE WHEN "CheckoutFormSent" = 0 THEN false ELSE true END;
                RAISE NOTICE 'Repaired Orders.CheckoutFormSent';
            END IF;
        END $$;
        """
    ];

    foreach (var sql in repairs)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
            repaired = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "SCHEMA REPAIR: non-fatal error during column repair");
        }
    }

    Log.Information(repaired ? "SCHEMA REPAIR APPLIED" : "SCHEMA REPAIR SKIPPED (no changes needed or tables don't exist yet)");
}

// ──────────────────────────────────────────
// Record a migration as applied in __EFMigrationsHistory
// ──────────────────────────────────────────
static void RecordMigrationAsApplied(System.Data.Common.DbConnection conn, string migrationId)
{
    try
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES (@mid, '8.0.11')
            ON CONFLICT ("MigrationId") DO NOTHING
            """;
        var param = cmd.CreateParameter();
        param.ParameterName = "@mid";
        param.Value = migrationId;
        cmd.Parameters.Add(param);
        cmd.ExecuteNonQuery();
        Log.Information("MIGRATE: recorded {Migration} in history", migrationId);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "MIGRATE: failed to record {Migration} in history", migrationId);
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
