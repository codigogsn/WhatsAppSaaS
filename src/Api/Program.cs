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
    MigrationRunner.Run(app.Services);

    // ────────────────────────────────────────
    // Seed default business, Zelle, founder
    // ────────────────────────────────────────
    DatabaseSeeder.RunAll(app.Services);

    // ── Production config warnings ──
    ConfigValidator.WarnIfMissing(app.Environment.EnvironmentName);

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
