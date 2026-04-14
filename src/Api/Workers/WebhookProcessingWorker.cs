using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Api.Workers;

public sealed class WebhookProcessingWorker : BackgroundService
{
    private readonly IMessageQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WebhookProcessingWorker> _logger;
    private readonly int _maxConcurrency;
    private readonly TimeSpan _processingTimeout;

    private const string FallbackMessage =
        "Lo sentimos, estamos experimentando un problema temporal. Por favor intenta de nuevo en unos minutos.";

    public WebhookProcessingWorker(
        IMessageQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<WebhookProcessingWorker> logger,
        IConfiguration config)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;

        _maxConcurrency = int.TryParse(
            config["WORKER_CONCURRENCY"] ?? Environment.GetEnvironmentVariable("WORKER_CONCURRENCY"),
            out var c) && c > 0 ? c : 1;

        var timeoutMin = int.TryParse(
            config["WORKER_TIMEOUT_MINUTES"] ?? Environment.GetEnvironmentVariable("WORKER_TIMEOUT_MINUTES"),
            out var t) && t > 0 ? t : 10;
        _processingTimeout = TimeSpan.FromMinutes(timeoutMin);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "WebhookProcessingWorker started concurrency={Concurrency} timeout={Timeout}",
            _maxConcurrency, _processingTimeout);

        if (_queue is not Infrastructure.Messaging.PostgresMessageQueue pgq)
        {
            // Fallback: original sequential loop for non-Postgres queues
            await RunSequentialAsync(stoppingToken);
            return;
        }

        using var semaphore = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
        var activeTasks = new List<Task>();
        var emptyPolls = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait for a concurrency slot
            try { await semaphore.WaitAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

            Infrastructure.Messaging.PostgresMessageQueue.DequeuedItem? item;
            try
            {
                item = await pgq.DequeueItemAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                semaphore.Release();
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error dequeuing from queue");
                semaphore.Release();
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }

            if (item is null)
            {
                semaphore.Release();
                emptyPolls++;
                var delay = emptyPolls >= 10 ? 1000 : 250;

                if (emptyPolls % 60 == 0)
                {
                    try { await CheckQueueHealthAsync(stoppingToken); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Queue health check failed (non-fatal)"); }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(delay), stoppingToken);
                continue;
            }

            emptyPolls = 0;

            // Spawn processing task — semaphore released when done
            var task = ProcessItemAsync(pgq, item, semaphore, stoppingToken);
            lock (activeTasks)
            {
                activeTasks.RemoveAll(t => t.IsCompleted);
                activeTasks.Add(task);
            }
        }

        // Graceful shutdown: wait for in-flight tasks
        Task[] remaining;
        lock (activeTasks) { remaining = activeTasks.Where(t => !t.IsCompleted).ToArray(); }
        if (remaining.Length > 0)
        {
            _logger.LogInformation("WebhookProcessingWorker shutting down, waiting for {Count} in-flight tasks", remaining.Length);
            await Task.WhenAll(remaining);
        }

        _logger.LogInformation("WebhookProcessingWorker stopped");
    }

    private async Task ProcessItemAsync(
        Infrastructure.Messaging.PostgresMessageQueue pgq,
        Infrastructure.Messaging.PostgresMessageQueue.DequeuedItem item,
        SemaphoreSlim semaphore,
        CancellationToken stoppingToken)
    {
        // Track current ownership marker — updated by heartbeat on each renewal
        var claimedAtUtc = item.ClaimedAtUtc;

        try
        {
            _logger.LogInformation(
                "Dequeued message for business {BusinessId} ({BusinessName})",
                item.Message.BusinessContext.BusinessId,
                item.Message.BusinessContext.BusinessName);

            // Create a per-item timeout linked to the stopping token.
            // Heartbeat failure also cancels this CTS to abort processing if ownership is lost.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeoutCts.CancelAfter(_processingTimeout);

            // Start heartbeat to keep claim alive during processing.
            // Heartbeat tracks and updates claimedAtUtc; cancels timeoutCts on ownership loss.
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var heartbeatTask = RunHeartbeatAsync(pgq, item.ItemId, claimedAtUtc, timeoutCts,
                newClaimed => claimedAtUtc = newClaimed, heartbeatCts.Token);

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<IWebhookProcessor>();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                await processor.ProcessAsync(item.Message.Payload, item.Message.BusinessContext, timeoutCts.Token);
                sw.Stop();

                // Stop heartbeat before completing
                await heartbeatCts.CancelAsync();

                var completed = await pgq.CompleteAsync(item.ItemId, claimedAtUtc, stoppingToken);
                if (completed)
                {
                    Diagnostics.AppMetrics.RecordProcessed(sw.Elapsed.TotalMilliseconds);
                    _logger.LogInformation(
                        "Processed message for business {BusinessId}",
                        item.Message.BusinessContext.BusinessId);
                }
                else
                {
                    _logger.LogWarning(
                        "Processed message for business {BusinessId} but ownership was lost — another worker may have handled it",
                        item.Message.BusinessContext.BusinessId);
                }
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                // Processing timeout or heartbeat ownership loss (not shutdown)
                await heartbeatCts.CancelAsync();
                var msg = $"Processing timed out after {_processingTimeout.TotalMinutes}m";
                _logger.LogError("WORKER TIMEOUT: {Message} for business {BusinessId}", msg, item.Message.BusinessContext.BusinessId);
                Diagnostics.AppMetrics.RecordFailed();

                try
                {
                    var (isTerminal, owned) = await pgq.FailAsync(item.ItemId, item.AttemptCount, claimedAtUtc, msg, stoppingToken);
                    if (isTerminal && owned)
                        await SendFallbackReplyAsync(item.Message, stoppingToken);
                }
                catch (Exception failEx) { _logger.LogError(failEx, "Failed to record timeout failure for queue item"); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown — don't fail the item, let it be re-claimed
                await heartbeatCts.CancelAsync();
                _logger.LogInformation("Processing interrupted by shutdown for business {BusinessId}", item.Message.BusinessContext.BusinessId);
            }
            catch (Exception ex)
            {
                await heartbeatCts.CancelAsync();
                _logger.LogError(ex, "Error processing queued webhook message");
                Diagnostics.AppMetrics.RecordFailed();

                try
                {
                    var (isTerminal, owned) = await pgq.FailAsync(item.ItemId, item.AttemptCount, claimedAtUtc, ex.Message, stoppingToken);
                    if (isTerminal && owned)
                        await SendFallbackReplyAsync(item.Message, stoppingToken);
                }
                catch (Exception failEx) { _logger.LogError(failEx, "Failed to return queue item for retry"); }

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            finally
            {
                // Ensure heartbeat is stopped
                if (!heartbeatCts.IsCancellationRequested)
                    await heartbeatCts.CancelAsync();
                try { await heartbeatTask; } catch { /* heartbeat cancellation is expected */ }
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Periodically renews the claim lease so long-running processing
    /// does not cause the item to be re-claimed by another cycle.
    /// Renews every 5 minutes (well within the 15-minute claim timeout).
    /// If renewal fails (ownership lost), cancels the processing CTS to abort the attempt.
    /// </summary>
    private async Task RunHeartbeatAsync(
        Infrastructure.Messaging.PostgresMessageQueue pgq,
        Guid itemId,
        DateTime initialClaimedAtUtc,
        CancellationTokenSource processingCts,
        Action<DateTime> onRenewed,
        CancellationToken ct)
    {
        var currentClaimedAt = initialClaimedAtUtc;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), ct);
                var newClaimed = await pgq.RenewClaimAsync(itemId, currentClaimedAt, ct);
                if (newClaimed is null)
                {
                    // Ownership lost — another worker reclaimed this item.
                    // Cancel processing to prevent silent loss on later failure.
                    _logger.LogWarning("QUEUE HEARTBEAT: ownership lost for item {Id} — aborting processing", itemId);
                    await processingCts.CancelAsync();
                    return;
                }
                currentClaimedAt = newClaimed.Value;
                onRenewed(currentClaimedAt);
                _logger.LogDebug("QUEUE HEARTBEAT: renewed claim for item {Id}", itemId);
            }
        }
        catch (OperationCanceledException) { /* expected when processing completes */ }
        catch (Exception ex)
        {
            // Treat heartbeat failure as ownership loss — abort processing
            _logger.LogWarning(ex, "QUEUE HEARTBEAT: failed to renew claim for item {Id} — aborting processing", itemId);
            try { await processingCts.CancelAsync(); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Fallback sequential loop for non-Postgres queues (InMemory dev mode).
    /// Preserves original behavior exactly.
    /// </summary>
    private async Task RunSequentialAsync(CancellationToken stoppingToken)
    {
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

                using var scope = _scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<IWebhookProcessor>();

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeoutCts.CancelAfter(_processingTimeout);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                await processor.ProcessAsync(message.Payload, message.BusinessContext, timeoutCts.Token);
                sw.Stop();

                if (_queue is Infrastructure.Messaging.PostgresMessageQueue pgq)
                    await pgq.CompleteLastAsync(stoppingToken);

                Diagnostics.AppMetrics.RecordProcessed(sw.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing queued webhook message");
                Diagnostics.AppMetrics.RecordFailed();

                if (_queue is Infrastructure.Messaging.PostgresMessageQueue pgqFail)
                {
                    try
                    {
                        var isTerminal = await pgqFail.FailLastAsync(ex.Message, stoppingToken);
                        if (isTerminal && message is not null)
                            await SendFallbackReplyAsync(message, stoppingToken);
                    }
                    catch (Exception failEx) { _logger.LogError(failEx, "Failed to return queue item for retry"); }
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        _logger.LogInformation("WebhookProcessingWorker stopped (sequential mode)");
    }

    private async Task SendFallbackReplyAsync(QueuedMessage message, CancellationToken ct)
    {
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
        if (_queue is not Infrastructure.Messaging.PostgresMessageQueue pgq) return;

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
