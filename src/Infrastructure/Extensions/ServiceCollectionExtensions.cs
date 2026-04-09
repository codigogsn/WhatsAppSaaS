using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Application.Services;
using WhatsAppSaaS.Application.Strategies;
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

        // ── Business insight thresholds ─────────────────
        services.AddOptions<BusinessInsightThresholds>()
            .Bind(configuration.GetSection(BusinessInsightThresholds.SectionName));

        // ── Payment Mobile options ────────────────────────
        var pmOpts = new PaymentMobileOptions();
        configuration.GetSection(PaymentMobileOptions.SectionName).Bind(pmOpts);
        services.AddSingleton(pmOpts);

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
        services.AddScoped<IMenuRepository, MenuRepository>();
        services.AddScoped<IExtrasRepository, ExtrasRepository>();
        services.AddScoped<IConversationStateStore, ConversationStateStore>();
        services.AddScoped<ISignatureValidator, SignatureValidator>();

        // ── Admin Analytics (✅ FIX runtime 500) ─────────
        services.AddScoped<IAdminAnalyticsService, AdminAnalyticsService>();

        // ── Business Insights (read-only intelligence layer) ──
        services.AddScoped<IBusinessInsightsService, BusinessInsightsService>();

        // ── Founder Insights (global read-only, Founder only) ──
        services.AddScoped<IFounderInsightsService, FounderInsightsService>();

        // ── Insights Chat (uses existing insights services + OpenAI) ──
        services.AddHttpClient<IInsightsChatService, InsightsChatService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });

        // ── BCV Exchange Rate ─────────────────────────────
        services.AddHttpClient<IBcvRateService, BcvRateService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddScoped<IExchangeRateProvider, ExchangeRateProvider>();

        // ── Gmail + Email AI ────────────────────────────
        services.AddHttpClient<GmailService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient<EmailAIService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });

        return services;
    }

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddSingleton<IVerticalStrategyFactory, VerticalStrategyFactory>();
        services.AddScoped<IBotService, BotService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IWebhookProcessor, WebhookProcessor>();

        return services;
    }
}
