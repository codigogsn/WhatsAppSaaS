namespace WhatsAppSaaS.Application.Interfaces;

public interface IBackgroundJobService
{
    Task EnqueueAsync(string jobType, object payload, Guid? businessId = null, int maxRetries = 3, CancellationToken ct = default);
}
