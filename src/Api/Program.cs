using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    // ✅ Forzar puerto local estable
    if (builder.Environment.IsDevelopment())
    {
        builder.WebHost.UseUrls("http://127.0.0.1:5070");
    }

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
    builder.Services.AddDbContextPool<AppDbContext>(options =>
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

        // ✅ SQLite fijo y consistente
        options.UseSqlite("Data Source=app.db");
    });

    builder.Services.AddApplicationServices();
    builder.Services.AddInfrastructure(builder.Configuration);
    builder.Services.AddHealthChecks();

    var app = builder.Build();

    // ────────────────────────────────────────
    // ✅ AUTO-MIGRATE (solo cuando hay Postgres / Render)
    // ────────────────────────────────────────
    using (var scope = app.Services.CreateScope())
    {
        var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var databaseUrl = cfg["DATABASE_URL"] ?? Environment.GetEnvironmentVariable("DATABASE_URL");

        if (!string.IsNullOrWhiteSpace(databaseUrl))
        {
            try
            {
                Log.Information("Applying EF migrations (Postgres)...");
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.Migrate();
                Log.Information("EF migrations applied successfully.");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Failed applying EF migrations on startup.");
                throw;
            }
        }
        else
        {
            Log.Information("DATABASE_URL not present -> skipping auto-migrate (SQLite dev).");
        }
    }

    app.UseGlobalExceptionHandling();
    app.UseRequestLogging();

    app.UseDefaultFiles();
    app.UseStaticFiles();

    app.MapControllers();
    app.MapHealthChecks("/health");

    // ✅ EF Tools guard:
    // Cuando ejecutamos "dotnet ef", NO levantamos el servidor.
    // EF tools solo necesita construir el host para acceder al DbContext.
    var isEfTooling = args.Any(a => a.Equals("ef", StringComparison.OrdinalIgnoreCase))
                      || Environment.GetEnvironmentVariable("DOTNET_EF") == "1";

    if (!isEfTooling)
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
