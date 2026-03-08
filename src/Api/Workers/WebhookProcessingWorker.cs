using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Api.Workers;

public sealed class WebhookProcessingWorker : BackgroundService
{
    private readonly IMessageQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WebhookProcessingWorker> _logger;

    public WebhookProcessingWorker(
        IMessageQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<WebhookProcessingWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WebhookProcessingWorker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var message = await _queue.DequeueAsync(stoppingToken);

                _logger.LogInformation(
                    "Dequeued message for business {BusinessId} ({BusinessName})",
                    message.BusinessContext.BusinessId,
                    message.BusinessContext.BusinessName);

                using var scope = _scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<IWebhookProcessor>();

                await processor.ProcessAsync(message.Payload, message.BusinessContext, stoppingToken);

                _logger.LogInformation(
                    "Processed message for business {BusinessId}",
                    message.BusinessContext.BusinessId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing queued webhook message");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }

        _logger.LogInformation("WebhookProcessingWorker stopped");
    }
}
