using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Serilog;
using WhatsAppSaaS.Api.Extensions;
using WhatsAppSaaS.Application.Validators;
using WhatsAppSaaS.Infrastructure.Extensions;
using WhatsAppSaaS.Infrastructure.Persistence;

// ──────────────────────────────────────────────
// Bootstrap Serilog early for startup logging
// ──────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting WhatsApp SaaS API");

    var builder = WebApplication.CreateBuilder(args);

    // ──────────────────────────────────────────────
    // Serilog
    // ──────────────────────────────────────────────
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "WhatsAppSaaS")
        .WriteTo.Console(
            outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} | {Message:lj}{NewLine}{Exception}")
        .WriteTo.File(
            path: "logs/whatsapp-saas-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30));

    // ──────────────────────────────────────────────
    // Services
    // ──────────────────────────────────────────────
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();

    // FluentValidation
    builder.Services.AddValidatorsFromAssemblyContaining<WebhookPayloadValidator>();

    // ✅ Register DbContext HERE (single source of truth)
    // Using pool reduces allocations and avoids weird dispose timing.
    builder.Services.AddDbContextPool<AppDbContext>(options =>
        options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

    // Application + Infrastructure layers
    builder.Services.AddApplicationServices();
    builder.Services.AddInfrastructure(builder.Configuration);

    // Health checks
    builder.Services.AddHealthChecks();

    // ──────────────────────────────────────────────
    // Build
    // ──────────────────────────────────────────────
    var app = builder.Build();

    // ──────────────────────────────────────────────
    // Middleware pipeline
    // ──────────────────────────────────────────────
    app.UseGlobalExceptionHandling();
    app.UseRequestLogging();
    app.UseSerilogRequestLogging();

    app.MapControllers();
    app.MapHealthChecks("/health");

    // ──────────────────────────────────────────────
    // Run
    // ──────────────────────────────────────────────
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

// Required for WebApplicationFactory in integration tests
namespace WhatsAppSaaS.Api
{
    public partial class Program { }
}
