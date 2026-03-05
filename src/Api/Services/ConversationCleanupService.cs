using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Api.Services;

public sealed class ConversationCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ConversationCleanupService> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(6);

    public ConversationCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<ConversationCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Interval, stoppingToken);

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IConversationStateStore>();
                await store.PurgeOldStatesAsync(Ttl, stoppingToken);
                _logger.LogDebug("Conversation state cleanup completed");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error during conversation state cleanup");
            }
        }
    }
}
