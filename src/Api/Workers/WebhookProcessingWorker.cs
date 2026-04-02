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

        var emptyPolls = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            QueuedMessage? message = null;
            try
            {
                message = await _queue.DequeueAsync(stoppingToken);

                if (message is null)
                {
                    emptyPolls++;
                    var delay = emptyPolls >= 10 ? 1000 : 250;
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), stoppingToken);
                    continue;
                }

                emptyPolls = 0;

                _logger.LogInformation(
                    "Dequeued message for business {BusinessId} ({BusinessName})",
                    message.BusinessContext.BusinessId,
                    message.BusinessContext.BusinessName);

                using var scope = _scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<IWebhookProcessor>();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                await processor.ProcessAsync(message.Payload, message.BusinessContext, stoppingToken);
                sw.Stop();

                if (_queue is WhatsAppSaaS.Infrastructure.Messaging.PostgresMessageQueue pgq)
                    await pgq.CompleteLastAsync(stoppingToken);

                Diagnostics.AppMetrics.RecordProcessed(sw.Elapsed.TotalMilliseconds);

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
                Diagnostics.AppMetrics.RecordFailed();

                if (_queue is WhatsAppSaaS.Infrastructure.Messaging.PostgresMessageQueue pgqFail)
                {
                    try { await pgqFail.FailLastAsync(ex.Message, stoppingToken); }
                    catch (Exception failEx) { _logger.LogError(failEx, "Failed to return queue item for retry"); }
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        _logger.LogInformation("WebhookProcessingWorker stopped");
    }
}
