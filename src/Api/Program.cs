using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
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
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting WhatsApp SaaS API");

    var builder = WebApplication.CreateBuilder(args);

    // ────────────────────────────────────────
    // Serilog
    // ────────────────────────────────────────
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "WhatsAppSaaS")
        .WriteTo.Console()
        .WriteTo.File(
            path: "logs/whatsapp-saas-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30));

    // ────────────────────────────────────────
    // Services
    // ────────────────────────────────────────
    builder.Services
        .AddControllers()
        .AddJsonOptions(o =>
        {
            // Evita error de ciclos si algún Entity viene con navegación circular
            o.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
            o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

    builder.Services.AddEndpointsApiExplorer();

    // FluentValidation
    builder.Services.AddValidatorsFromAssemblyContaining<WebhookPayloadValidator>();

    // ────────────────────────────────────────
    // DbContext (PostgreSQL via Render DATABASE_URL)
    // IMPORTANT:
    // - In Production we REQUIRE DATABASE_URL
    // - In Dev we can fallback to SQLite
    // ────────────────────────────────────────
    builder.Services.AddDbContextPool<AppDbContext>(options =>
    {
        var env = builder.Environment.EnvironmentName;

        // Render env vars also flow into builder.Configuration automatically
        var databaseUrl = builder.Configuration["DATABASE_URL"]
                          ?? Environment.GetEnvironmentVariable("DATABASE_URL");

        if (!string.IsNullOrWhiteSpace(databaseUrl))
        {
            var connString = ConvertDatabaseUrlToNpgsql(databaseUrl);
            options.UseNpgsql(connString);
            return;
        }

        // If we're in Production and DATABASE_URL is missing, FAIL FAST (don’t silently use SQLite)
        if (string.Equals(env, "Production", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("DATABASE_URL is missing in Production. Refusing to start with SQLite.");
        }

        // Local dev fallback
        options.UseSqlite(builder.Configuration.GetConnectionString("Default"));
    });

    // Application + Infrastructure layers
    builder.Services.AddApplicationServices();
    builder.Services.AddInfrastructure(builder.Configuration);

    // Health checks
    builder.Services.AddHealthChecks();

    // ────────────────────────────────────────
    // Build
    // ────────────────────────────────────────
    var app = builder.Build();

    // ────────────────────────────────────────
    // Middleware pipeline
    // ────────────────────────────────────────
    app.UseGlobalExceptionHandling();
    app.UseRequestLogging();

    // ✅ Sirve wwwroot/index.html en "/"
    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.MapControllers();
    app.MapHealthChecks("/health");

    // ────────────────────────────────────────
    // Run
    // ────────────────────────────────────────
    app.Run();
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
// Helper: Convert Render DATABASE_URL -> Npgsql connection string
// Fix: uri.Port can be -1 for "postgresql" scheme in .NET, so we default to 5432.
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

// Required for WebApplicationFactory in integration tests
namespace WhatsAppSaaS.Api
{
    public partial class Program { }
}
