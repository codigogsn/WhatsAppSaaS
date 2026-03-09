namespace WhatsAppSaaS.Application.Interfaces;

/// <summary>
/// Resolved exchange rate for display purposes.
/// </summary>
public sealed record ResolvedRate(
    decimal Rate,
    string CurrencyLabel,
    DateTime RateDate,
    bool IsStale);

public interface IExchangeRateProvider
{
    /// <summary>
    /// Get the applicable BCV rate for a business's currency reference.
    /// Returns null if reference is "NONE" or no rate exists at all.
    /// </summary>
    Task<ResolvedRate?> GetRateAsync(string? currencyReference, CancellationToken ct = default);
}
