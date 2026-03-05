using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Application.Services;
using WhatsAppSaaS.Infrastructure.Ai;
using WhatsAppSaaS.Infrastructure.Persistence;
using WhatsAppSaaS.Infrastructure.Repositories;
using WhatsAppSaaS.Infrastructure.Services;
using WhatsAppSaaS.Infrastructure.WhatsApp;

namespace WhatsAppSaaS.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── WhatsApp options ────────────────────────────
        services.AddOptions<WhatsAppOptions>()
            .Bind(configuration.GetSection(WhatsAppOptions.SectionName));

        // ── AiParser options ────────────────────────────
        services.AddOptions<AiParserOptions>()
            .Bind(configuration.GetSection(AiParserOptions.SectionName));

        // ── WhatsApp typed HttpClient ───────────────────
        services.AddHttpClient<IWhatsAppClient, WhatsAppClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            // Production: consider certificate pinning
        });

        // ── AiParser typed HttpClient (OpenAI) ──────────
        services.AddHttpClient<IAiParser, AiParser>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(
                configuration.GetValue($"{AiParserOptions.SectionName}:TimeoutSeconds", 30));
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });

        // ── Repos / infra services ──────────────────────
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IConversationStateStore, ConversationStateStore>();
        services.AddScoped<ISignatureValidator, SignatureValidator>();

        // ── Admin Analytics (✅ FIX runtime 500) ─────────
        services.AddScoped<IAdminAnalyticsService, AdminAnalyticsService>();

        return services;
    }

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<IBotService, BotService>();
        services.AddScoped<IWebhookProcessor, WebhookProcessor>();

        return services;
    }
}
