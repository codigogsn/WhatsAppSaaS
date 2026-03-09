using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Infrastructure.Services;

public sealed class ExchangeRateProvider : IExchangeRateProvider
{
    private readonly AppDbContext _db;
    private readonly ILogger<ExchangeRateProvider> _logger;

    public ExchangeRateProvider(AppDbContext db, ILogger<ExchangeRateProvider> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ResolvedRate?> GetRateAsync(string? currencyReference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(currencyReference) || currencyReference == "NONE")
            return null;

        var today = DateTime.UtcNow.Date;

        // Try today first, then fallback to most recent
        var rate = await _db.ExchangeRates
            .Where(r => r.RateDate == today)
            .FirstOrDefaultAsync(ct);

        var isStale = false;

        if (rate is null)
        {
            rate = await _db.ExchangeRates
                .OrderByDescending(r => r.RateDate)
                .FirstOrDefaultAsync(ct);

            if (rate is null)
            {
                _logger.LogWarning("No exchange rates found in DB — business currency ref {Ref} cannot be resolved", currencyReference);
                return null;
            }

            isStale = true;
            _logger.LogWarning("Using STALE BCV rate from {Date} (today={Today}) — no rate fetched for today",
                rate.RateDate.ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"));
        }

        return currencyReference switch
        {
            "BCV_USD" => new ResolvedRate(rate.UsdRate, "USD", rate.RateDate, isStale),
            "BCV_EUR" => new ResolvedRate(rate.EurRate, "EUR", rate.RateDate, isStale),
            _ => null
        };
    }
}
