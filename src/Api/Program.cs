using System;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using WhatsAppSaaS.Api.Extensions;
using WhatsAppSaaS.Api.Services;
using WhatsAppSaaS.Api.Startup;
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

    // ────────────────────────────────────────
    // AllowedHosts: restrict in production, permissive in dev
    // ────────────────────────────────────────
    var allowedHosts = Environment.GetEnvironmentVariable("ALLOWED_HOSTS");
    if (string.IsNullOrWhiteSpace(allowedHosts) && !builder.Environment.IsDevelopment())
    {
        // Auto-detect from Render's RENDER_EXTERNAL_URL (e.g. "https://myapp.onrender.com")
        var renderUrl = Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL");
        if (!string.IsNullOrWhiteSpace(renderUrl) && Uri.TryCreate(renderUrl, UriKind.Absolute, out var renderUri))
            allowedHosts = renderUri.Host;
    }
    if (!string.IsNullOrWhiteSpace(allowedHosts))
    {
        builder.Configuration["AllowedHosts"] = allowedHosts;
        Log.Information("ALLOWED HOSTS: {Hosts}", allowedHosts);
    }
    else if (builder.Environment.IsDevelopment())
    {
        Log.Information("ALLOWED HOSTS: * (development mode)");
    }
    else
    {
        Log.Warning("ALLOWED HOSTS: * (no ALLOWED_HOSTS env var and RENDER_EXTERNAL_URL not set — consider restricting)");
    }

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
    var databaseUrl = builder.Configuration["DATABASE_URL"]
                      ?? Environment.GetEnvironmentVariable("DATABASE_URL");
    string? npgsqlConnString = !string.IsNullOrWhiteSpace(databaseUrl)
        ? ConvertDatabaseUrlToNpgsql(databaseUrl)
        : null;

    builder.Services.AddDbContext<AppDbContext>(options =>
    {
        if (npgsqlConnString is not null)
        {
            options.UseNpgsql(npgsqlConnString);
            return;
        }

        if (string.Equals(builder.Environment.EnvironmentName, "Production", StringComparison.OrdinalIgnoreCase))
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

    // Async webhook processing queue — PostgreSQL-backed when available, in-memory fallback for dev
    if (npgsqlConnString is not null)
    {
        builder.Services.AddSingleton<WhatsAppSaaS.Application.Interfaces.IMessageQueue>(sp =>
            new WhatsAppSaaS.Infrastructure.Messaging.PostgresMessageQueue(
                npgsqlConnString,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<WhatsAppSaaS.Infrastructure.Messaging.PostgresMessageQueue>()));
        Log.Information("MESSAGE QUEUE: PostgreSQL-backed persistent queue enabled");
    }
    else
    {
        builder.Services.AddSingleton<WhatsAppSaaS.Application.Interfaces.IMessageQueue,
            WhatsAppSaaS.Infrastructure.Messaging.InMemoryMessageQueue>();
        Log.Warning("MESSAGE QUEUE: using in-memory queue — messages in flight will be lost on container restart/deploy");
    }
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
    // Data Protection (key persistence + optional encryption)
    // ────────────────────────────────────────
    var dpKeysPath = Environment.GetEnvironmentVariable("DATA_PROTECTION_KEYS_PATH");
    var dpCertPath = Environment.GetEnvironmentVariable("DATA_PROTECTION_CERTIFICATE_PATH");
    var dpConfigured = false;

    if (string.IsNullOrWhiteSpace(dpKeysPath))
    {
        Log.Error("DATA PROTECTION ERROR: DATA_PROTECTION_KEYS_PATH env var is not set — keys will NOT persist across deploys. Set this to a Render persistent disk path.");
    }
    else
    {
        try
        {
            Directory.CreateDirectory(dpKeysPath);

            // Verify the directory is writable
            var testFile = Path.Combine(dpKeysPath, ".dp-write-test");
            File.WriteAllText(testFile, "ok");
            File.Delete(testFile);

            var dpBuilder = builder.Services.AddDataProtection()
                .SetApplicationName("CODIGO-WhatsAppSaaS")
                .PersistKeysToFileSystem(new DirectoryInfo(dpKeysPath))
                .SetDefaultKeyLifetime(TimeSpan.FromDays(90));

            // Optional: encrypt keys at rest with an X.509 certificate
            if (!string.IsNullOrWhiteSpace(dpCertPath))
            {
                // Path explicitly configured — must work or fail fast
                if (!File.Exists(dpCertPath))
                    throw new FileNotFoundException(
                        $"DATA_PROTECTION_CERTIFICATE_PATH is set to '{dpCertPath}' but the file does not exist. " +
                        "Fix the path or remove the variable to run without encryption at rest.");

                var dpCertPassword = Environment.GetEnvironmentVariable("DATA_PROTECTION_CERTIFICATE_PASSWORD");
                var cert = string.IsNullOrWhiteSpace(dpCertPassword)
                    ? new System.Security.Cryptography.X509Certificates.X509Certificate2(dpCertPath)
                    : new System.Security.Cryptography.X509Certificates.X509Certificate2(dpCertPath, dpCertPassword);
                dpBuilder.ProtectKeysWithCertificate(cert);
                Log.Information("DATA PROTECTION: keys encrypted at rest with certificate thumbprint={Thumbprint}", cert.Thumbprint);
            }
            else
            {
                Log.Warning("DATA PROTECTION: keys persisted but NOT encrypted at rest. Set DATA_PROTECTION_CERTIFICATE_PATH to a .pfx file to enable encryption.");
            }

            dpConfigured = true;
            Log.Information("DATA PROTECTION: path={Path} | writable=true | keyLifetime=90d | app=CODIGO-WhatsAppSaaS", dpKeysPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DATA PROTECTION ERROR: path {Path} is not writable — persistence not guaranteed", dpKeysPath);
        }
    }

    if (!dpConfigured)
    {
        // Register Data Protection with defaults so the app still runs
        builder.Services.AddDataProtection()
            .SetApplicationName("CODIGO-WhatsAppSaaS");
        Log.Error("DATA PROTECTION: running with default (non-persistent) key storage — sessions/tokens WILL break on restart");
    }

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

        // Admin: 120 requests per minute per IP
        // (dashboard auto-refresh fires ~5 parallel requests every 10s ≈ 30/min baseline)
        options.AddPolicy("admin", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 120,
                    Window = TimeSpan.FromMinutes(1)
                }));

        // Login: 10 requests per minute per IP (brute-force protection)
        options.AddPolicy("login", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1)
                }));
    });

    var app = builder.Build();

    // ────────────────────────────────────────
    // DEBUG: DB info endpoint (Development only)
    // ────────────────────────────────────────
    if (app.Environment.IsDevelopment())
    {
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
    }

    // ────────────────────────────────────────
    // AUTO-MIGRATE (both SQLite and Postgres)
    // ────────────────────────────────────────
    MigrationRunner.Run(app.Services);

    // ────────────────────────────────────────
    // Seed default business, Zelle, founder
    // ────────────────────────────────────────
    DatabaseSeeder.RunAll(app.Services);

    // ── Database connectivity check ──
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();
        Log.Information("DB CHECK: PostgreSQL connected | Provider: {Provider}", db.Database.ProviderName ?? "Npgsql");
        await conn.CloseAsync();
    }
    catch (Exception ex)
    {
        Log.Error(ex, "DB CHECK: Failed to connect to database");
    }

    // ── Production config warnings ──
    ConfigValidator.WarnIfMissing(app.Environment.EnvironmentName);

    Log.Information("BACKUP REMINDER: Ensure Render PostgreSQL backups are enabled");

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
    app.UseMiddleware<WhatsAppSaaS.Api.Middleware.JwtFallbackMiddleware>();
    app.UseMiddleware<WhatsAppSaaS.Api.Middleware.TokenVersionMiddleware>();
    app.UseAuthorization();

    app.MapControllers();

    // Public health endpoint — minimal safe response for uptime checks
    var startTime = DateTime.UtcNow;
    app.MapGet("/health", async (AppDbContext db) =>
    {
        var dbOk = false;
        try
        {
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            dbOk = true;
            await conn.CloseAsync();
        }
        catch { }

        return Results.Ok(new
        {
            status = dbOk ? "Healthy" : "Degraded",
            timestamp = DateTime.UtcNow.ToString("O")
        });
    });

    // Admin-protected detailed health endpoint
    app.MapGet("/health/detailed", async (HttpContext ctx, AppDbContext db) =>
    {
        // Reuse X-Admin-Key protection
        var adminKey = Environment.GetEnvironmentVariable("ADMIN_KEY");
        if (string.IsNullOrWhiteSpace(adminKey))
            return Results.Json(new { error = "ADMIN_KEY not configured" }, statusCode: 500);

        if (!ctx.Request.Headers.TryGetValue("X-Admin-Key", out var headerKey)
            || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(headerKey.ToString()),
                Encoding.UTF8.GetBytes(adminKey)))
            return Results.Json(new { error = "Missing or invalid X-Admin-Key" }, statusCode: 401);

        var dbStatus = "Disconnected";
        long queuePending = 0, stuckItems = 0;
        string? lastProcessed = null;
        try
        {
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            dbStatus = "Connected";

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    COUNT(*) FILTER (WHERE "ProcessedAtUtc" IS NULL AND "AttemptCount" < 5) AS pending,
                    COUNT(*) FILTER (WHERE "ProcessedAtUtc" IS NULL AND "AttemptCount" >= 5) AS stuck,
                    MAX("ProcessedAtUtc") AS last_processed
                FROM "WebhookQueue"
            """;
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                queuePending = reader.GetInt64(0);
                stuckItems = reader.GetInt64(1);
                lastProcessed = reader.IsDBNull(2) ? null : reader.GetDateTime(2).ToString("O");
            }
            await conn.CloseAsync();
        }
        catch { }

        return Results.Ok(new
        {
            status = "Healthy",
            db = dbStatus,
            uptime = (DateTime.UtcNow - startTime).ToString(@"d\.hh\:mm\:ss"),
            queuePendingCount = queuePending,
            stuckQueueItems = stuckItems,
            lastProcessedAtUtc = lastProcessed,
            workerMode = "single-instance",
            maxPoolSize = 25,
            openAiConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
        });
    });

    // Prometheus-compatible metrics endpoint (plain text, admin-protected)
    var metricsConnString = npgsqlConnString;
    app.MapGet("/metrics", async (HttpContext ctx) =>
    {
        var metricsAdminKey = Environment.GetEnvironmentVariable("ADMIN_KEY");
        if (string.IsNullOrWhiteSpace(metricsAdminKey))
            return Results.Json(new { error = "ADMIN_KEY not configured" }, statusCode: 500);

        if (!ctx.Request.Headers.TryGetValue("X-Admin-Key", out var metricsHeaderKey)
            || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(metricsHeaderKey.ToString()),
                Encoding.UTF8.GetBytes(metricsAdminKey)))
            return Results.Json(new { error = "Missing or invalid X-Admin-Key" }, statusCode: 401);

        long queuePending = 0;
        if (metricsConnString is not null)
        {
            try
            {
                await using var conn = new Npgsql.NpgsqlConnection(metricsConnString);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """SELECT COUNT(*) FROM "WebhookQueue" WHERE "ProcessedAtUtc" IS NULL AND "AttemptCount" < 5""";
                queuePending = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }
            catch { }
        }

        return Results.Text(
            WhatsAppSaaS.Api.Diagnostics.AppMetrics.RenderPrometheus(queuePending),
            "text/plain; version=0.0.4");
    });

    if (!EF.IsDesignTime)
    {
        // Start listening FIRST so Render health check passes immediately
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
// Render DATABASE_URL helper
// ──────────────────────────────────────────
static string ConvertDatabaseUrlToNpgsql(string databaseUrl)
{
    Uri uri;
    try
    {
        uri = new Uri(databaseUrl);
    }
    catch (UriFormatException ex)
    {
        Log.Fatal("DATABASE_URL is malformed: {Message}", ex.Message);
        throw;
    }

    var userInfo = uri.UserInfo.Split(':', 2);
    var username = Uri.UnescapeDataString(userInfo[0]);
    var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";

    var host = uri.Host;
    var port = uri.Port > 0 ? uri.Port : 5432;
    var database = uri.AbsolutePath.TrimStart('/');

    return $"Host={host};Port={port};Database={database};Username={username};Password={password};Ssl Mode=Require;Trust Server Certificate=true;Maximum Pool Size=25";
}

namespace WhatsAppSaaS.Api
{
    public partial class Program { }
}
