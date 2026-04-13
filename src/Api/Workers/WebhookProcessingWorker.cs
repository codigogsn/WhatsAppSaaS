using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Api.Workers;

public sealed class WebhookProcessingWorker : BackgroundService
{
    private readonly IMessageQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WebhookProcessingWorker> _logger;

    private const string FallbackMessage =
        "Lo sentimos, estamos experimentando un problema temporal. Por favor intenta de nuevo en unos minutos.";

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

                    // Periodic queue health check (~every 60s of idle)
                    if (emptyPolls % 60 == 0 && _queue is WhatsAppSaaS.Infrastructure.Messaging.PostgresMessageQueue)
                    {
                        try { await CheckQueueHealthAsync(stoppingToken); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Queue health check failed (non-fatal)"); }
                    }

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
                    try
                    {
                        var isTerminal = await pgqFail.FailLastAsync(ex.Message, stoppingToken);
                        if (isTerminal && message is not null)
                        {
                            await SendFallbackReplyAsync(message, stoppingToken);
                        }
                    }
                    catch (Exception failEx) { _logger.LogError(failEx, "Failed to return queue item for retry"); }
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        _logger.LogInformation("WebhookProcessingWorker stopped");
    }

    private async Task SendFallbackReplyAsync(QueuedMessage message, CancellationToken ct)
    {
        // Extract the customer phone from the first inbound message in the payload
        var customerPhone = message.Payload.Entry
            .SelectMany(e => e.Changes)
            .Select(c => c.Value)
            .Where(v => v?.Messages is { Count: > 0 })
            .SelectMany(v => v!.Messages!)
            .Select(m => m.From)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(customerPhone))
        {
            _logger.LogWarning(
                "FALLBACK SKIP: no customer phone found in payload for business {BusinessId}",
                message.BusinessContext.BusinessId);
            return;
        }

        _logger.LogWarning(
            "FALLBACK ATTEMPT: sending fallback reply to {CustomerPhone} for business {BusinessId} after permanent processing failure",
            customerPhone, message.BusinessContext.BusinessId);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var whatsApp = scope.ServiceProvider.GetRequiredService<IWhatsAppClient>();

            var fallback = new OutgoingMessage
            {
                To = customerPhone,
                Body = FallbackMessage,
                PhoneNumberId = message.BusinessContext.PhoneNumberId,
                AccessToken = message.BusinessContext.AccessToken
            };

            var sent = await whatsApp.SendTextMessageAsync(fallback, ct);

            if (sent)
            {
                _logger.LogWarning(
                    "FALLBACK SENT: fallback reply delivered to {CustomerPhone} for business {BusinessId}",
                    customerPhone, message.BusinessContext.BusinessId);
            }
            else
            {
                _logger.LogError(
                    "FALLBACK FAILED: WhatsApp API rejected fallback to {CustomerPhone} for business {BusinessId}",
                    customerPhone, message.BusinessContext.BusinessId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "FALLBACK ERROR: exception sending fallback to {CustomerPhone} for business {BusinessId}",
                customerPhone, message.BusinessContext.BusinessId);
        }
    }

    private async Task CheckQueueHealthAsync(CancellationToken ct)
    {
        if (_queue is not WhatsAppSaaS.Infrastructure.Messaging.PostgresMessageQueue pgq) return;

        await using var conn = new Npgsql.NpgsqlConnection(pgq.ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*) FILTER (WHERE "ProcessedAtUtc" IS NULL AND "AttemptCount" < 5) AS pending,
                COUNT(*) FILTER (WHERE "ProcessedAtUtc" IS NULL AND "AttemptCount" >= 5) AS stuck
            FROM "WebhookQueue"
        """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return;

        var pending = reader.GetInt64(0);
        var stuck = reader.GetInt64(1);

        if (stuck > 0)
        {
            var msg = $"QUEUE ALERT: stuck items detected count={stuck}";
            _logger.LogWarning(msg);
            Diagnostics.AlertDispatcher.TrySendAlert(msg, Microsoft.Extensions.Logging.LogLevel.Warning);
        }

        if (pending >= 200)
        {
            var msg = $"QUEUE ALERT: pending backlog critical count={pending}";
            _logger.LogError(msg);
            Diagnostics.AlertDispatcher.TrySendAlert(msg, Microsoft.Extensions.Logging.LogLevel.Error);
        }
        else if (pending >= 50)
        {
            var msg = $"QUEUE ALERT: pending backlog warning count={pending}";
            _logger.LogWarning(msg);
            Diagnostics.AlertDispatcher.TrySendAlert(msg, Microsoft.Extensions.Logging.LogLevel.Warning);
        }
    }
}
