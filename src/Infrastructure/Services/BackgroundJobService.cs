using System.Text.Json;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Infrastructure.Services;

public sealed class BackgroundJobService : IBackgroundJobService
{
    private readonly AppDbContext _db;

    public BackgroundJobService(AppDbContext db)
    {
        _db = db;
    }

    public async Task EnqueueAsync(string jobType, object payload, Guid? businessId = null, int maxRetries = 3, CancellationToken ct = default)
    {
        _db.BackgroundJobs.Add(new BackgroundJob
        {
            JobType = jobType,
            PayloadJson = JsonSerializer.Serialize(payload),
            BusinessId = businessId,
            MaxRetries = maxRetries,
            Status = "Pending",
            ScheduledAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }
}
