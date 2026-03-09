using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Application.Interfaces;

public interface IBcvRateService
{
    /// <summary>
    /// Fetch today's rates from BCV and persist to DB.
    /// Returns the persisted entity, or null if fetching failed.
    /// </summary>
    Task<ExchangeRate?> FetchAndPersistTodayAsync(CancellationToken ct = default);
}
