using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Infrastructure.Persistence;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Api.Workers;

public sealed class CleanupJobScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CleanupJobScheduler> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public CleanupJobScheduler(IServiceScopeFactory scopeFactory, ILogger<CleanupJobScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CleanupJobScheduler started");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Interval, stoppingToken);

            try
            {
                await ScheduleCleanupJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scheduling cleanup jobs");
            }
        }
    }

    private async Task ScheduleCleanupJobsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var jobTypes = new[] { "CleanupConversations", "CleanupCompletedJobs", "CleanupAbandonedOrders", "FetchBcvRates" };

        foreach (var jobType in jobTypes)
        {
            // Don't schedule if one is already pending/processing
            var exists = await db.BackgroundJobs
                .AnyAsync(j => j.JobType == jobType && (j.Status == "Pending" || j.Status == "Processing"), ct);

            if (!exists)
            {
                db.BackgroundJobs.Add(new BackgroundJob
                {
                    JobType = jobType,
                    Status = "Pending",
                    MaxRetries = 2,
                    ScheduledAtUtc = DateTime.UtcNow
                });
            }
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Cleanup jobs scheduled");
    }
}
