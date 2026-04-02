using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Workers;

public sealed class BackgroundJobWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BackgroundJobWorker> _logger;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(5);

    public BackgroundJobWorker(IServiceScopeFactory scopeFactory, ILogger<BackgroundJobWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BackgroundJobWorker started");
        _logger.LogWarning("BackgroundJobWorker running in single-instance mode. Horizontal scaling requires FOR UPDATE SKIP LOCKED.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessNextJobAsync(stoppingToken);
                if (!processed)
                    await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BackgroundJobWorker loop error");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }

        _logger.LogInformation("BackgroundJobWorker stopped");
    }

    private async Task<bool> ProcessNextJobAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Acquire next pending job (or stuck Processing job past lock timeout)
        var lockCutoff = DateTime.UtcNow - LockTimeout;
        var now = DateTime.UtcNow;

        var job = await db.BackgroundJobs
            .Where(j =>
                (j.Status == "Pending" && j.ScheduledAtUtc <= now) ||
                (j.Status == "Processing" && j.LockedAtUtc < lockCutoff))
            .OrderBy(j => j.ScheduledAtUtc)
            .FirstOrDefaultAsync(ct);

        if (job is null) return false;

        // Lock the job
        job.Status = "Processing";
        job.LockedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Processing job {JobId} type={JobType} attempt={Attempt}",
            job.Id, job.JobType, job.RetryCount + 1);

        try
        {
            await ExecuteJobAsync(job, scope.ServiceProvider, ct);

            job.Status = "Done";
            job.CompletedAtUtc = DateTime.UtcNow;
            job.LastError = null;
            await db.SaveChangesAsync(ct);

            _logger.LogInformation("Job {JobId} completed successfully", job.Id);
        }
        catch (Exception ex)
        {
            job.RetryCount++;
            job.LastError = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;

            if (job.RetryCount >= job.MaxRetries)
            {
                job.Status = "Failed";
                job.CompletedAtUtc = DateTime.UtcNow;
                _logger.LogError(ex, "Job {JobId} failed permanently after {Retries} retries", job.Id, job.RetryCount);
            }
            else
            {
                // Exponential backoff: 30s, 120s, 480s
                var delaySec = 30 * Math.Pow(4, job.RetryCount - 1);
                job.Status = "Pending";
                job.ScheduledAtUtc = DateTime.UtcNow.AddSeconds(delaySec);
                job.LockedAtUtc = null;
                _logger.LogWarning(ex, "Job {JobId} failed, retry {Retry}/{Max} scheduled at {ScheduledAt}",
                    job.Id, job.RetryCount, job.MaxRetries, job.ScheduledAtUtc);
            }

            await db.SaveChangesAsync(ct);
        }

        return true;
    }

    private async Task ExecuteJobAsync(BackgroundJob job, IServiceProvider sp, CancellationToken ct)
    {
        switch (job.JobType)
        {
            case "SendNotification":
                await ExecuteSendNotificationAsync(job, sp, ct);
                break;

            case "CleanupConversations":
                await ExecuteCleanupConversationsAsync(sp, ct);
                break;

            case "CleanupCompletedJobs":
                await ExecuteCleanupCompletedJobsAsync(sp, ct);
                break;

            case "CleanupAbandonedOrders":
                await ExecuteCleanupAbandonedOrdersAsync(sp, ct);
                break;

            case "FetchBcvRates":
                await ExecuteFetchBcvRatesAsync(sp, ct);
                break;

            default:
                _logger.LogWarning("Unknown job type: {JobType}", job.JobType);
                break;
        }
    }

    private async Task ExecuteSendNotificationAsync(BackgroundJob job, IServiceProvider sp, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<NotificationPayload>(job.PayloadJson);
        if (payload is null)
            throw new InvalidOperationException("Invalid notification payload");

        var client = sp.GetRequiredService<IWhatsAppClient>();
        var success = await client.SendTextMessageAsync(new OutgoingMessage
        {
            To = payload.To,
            Body = payload.Body,
            PhoneNumberId = payload.PhoneNumberId,
            AccessToken = payload.AccessToken
        }, ct);

        if (!success)
            throw new InvalidOperationException($"WhatsApp send failed to {payload.To}");
    }

    private async Task ExecuteCleanupConversationsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var store = sp.GetRequiredService<IConversationStateStore>();
        await store.PurgeOldStatesAsync(TimeSpan.FromHours(6), ct);
        _logger.LogInformation("Conversation cleanup completed");
    }

    private async Task ExecuteCleanupCompletedJobsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var cutoff = DateTime.UtcNow.AddDays(-7);
        var old = await db.BackgroundJobs
            .Where(j => (j.Status == "Done" || j.Status == "Failed") && j.CompletedAtUtc < cutoff)
            .ToListAsync(ct);

        if (old.Count > 0)
        {
            db.BackgroundJobs.RemoveRange(old);
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Cleaned up {Count} old completed/failed jobs", old.Count);
        }

        // Clean processed WebhookQueue rows older than 7 days
        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM "WebhookQueue"
                WHERE "ProcessedAtUtc" IS NOT NULL
                  AND "ProcessedAtUtc" < now() - interval '7 days'
            """;
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            if (deleted > 0)
                _logger.LogInformation("QUEUE CLEANUP: deleted {Count} processed WebhookQueue rows older than 7 days", deleted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QUEUE CLEANUP: failed to clean WebhookQueue (non-fatal)");
        }

        // Purge abandoned items (exhausted all retries, older than 7 days)
        try
        {
            var conn2 = db.Database.GetDbConnection();
            if (conn2.State != System.Data.ConnectionState.Open) await conn2.OpenAsync(ct);
            using var cmd2 = conn2.CreateCommand();
            cmd2.CommandText = """
                DELETE FROM "WebhookQueue"
                WHERE "ProcessedAtUtc" IS NULL
                  AND "AttemptCount" >= 5
                  AND "CreatedAtUtc" < now() - interval '7 days'
            """;
            var abandoned = await cmd2.ExecuteNonQueryAsync(ct);
            if (abandoned > 0)
                _logger.LogInformation("QUEUE CLEANUP: purged {Count} abandoned items", abandoned);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QUEUE CLEANUP: failed to purge abandoned items (non-fatal)");
        }
    }

    private async Task ExecuteCleanupAbandonedOrdersAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var cutoff = DateTime.UtcNow.AddHours(-24);

        // Find orders stuck in Pending with no checkout for 24+ hours
        var abandoned = await db.Orders
            .Where(o => o.Status == "Pending"
                && !o.CheckoutCompleted
                && o.CreatedAtUtc < cutoff)
            .ToListAsync(ct);

        foreach (var order in abandoned)
        {
            order.Status = "Cancelled";
        }

        if (abandoned.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Cancelled {Count} abandoned orders older than 24h", abandoned.Count);
        }
    }

    private async Task ExecuteFetchBcvRatesAsync(IServiceProvider sp, CancellationToken ct)
    {
        var bcvService = sp.GetRequiredService<IBcvRateService>();
        var result = await bcvService.FetchAndPersistTodayAsync(ct);

        if (result is null)
            throw new InvalidOperationException("BCV rate fetch returned null — will retry");

        _logger.LogInformation("BCV rates fetched — USD={Usd} EUR={Eur} date={Date}",
            result.UsdRate, result.EurRate, result.RateDate.ToString("yyyy-MM-dd"));
    }

    public sealed class NotificationPayload
    {
        public string To { get; set; } = "";
        public string Body { get; set; } = "";
        public string PhoneNumberId { get; set; } = "";
        public string? AccessToken { get; set; }
    }
}
