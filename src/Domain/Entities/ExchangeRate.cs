namespace WhatsAppSaaS.Domain.Entities;

public class ExchangeRate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Date this rate applies to (UTC date, no time component).</summary>
    public DateTime RateDate { get; set; }

    /// <summary>BCV USD/VES rate (e.g. 36.50 means 1 USD = 36.50 Bs).</summary>
    public decimal UsdRate { get; set; }

    /// <summary>BCV EUR/VES rate (e.g. 40.20 means 1 EUR = 40.20 Bs).</summary>
    public decimal EurRate { get; set; }

    /// <summary>Where the rate came from: "bcv", "manual", etc.</summary>
    public string Source { get; set; } = "bcv";

    public DateTime FetchedAtUtc { get; set; } = DateTime.UtcNow;
}
