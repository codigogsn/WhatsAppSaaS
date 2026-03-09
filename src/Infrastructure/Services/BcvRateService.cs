using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Infrastructure.Services;

public sealed class BcvRateService : IBcvRateService
{
    private readonly AppDbContext _db;
    private readonly HttpClient _http;
    private readonly ILogger<BcvRateService> _logger;

    private const string BcvUrl = "https://www.bcv.org.ve/";

    public BcvRateService(AppDbContext db, HttpClient http, ILogger<BcvRateService> logger)
    {
        _db = db;
        _http = http;
        _logger = logger;
    }

    public async Task<ExchangeRate?> FetchAndPersistTodayAsync(CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;

        // Check if we already have today's rate
        var existing = await _db.ExchangeRates
            .FirstOrDefaultAsync(r => r.RateDate == today, ct);

        if (existing is not null)
        {
            _logger.LogInformation("BCV rate for {Date} already exists — USD={Usd} EUR={Eur}",
                today.ToString("yyyy-MM-dd"), existing.UsdRate, existing.EurRate);
            return existing;
        }

        // Fetch from BCV
        try
        {
            var (usd, eur) = await ScrapeBcvAsync(ct);

            if (usd <= 0 || eur <= 0)
            {
                _logger.LogError("BCV scrape returned invalid rates — USD={Usd} EUR={Eur}", usd, eur);
                return null;
            }

            var rate = new ExchangeRate
            {
                RateDate = today,
                UsdRate = usd,
                EurRate = eur,
                Source = "bcv",
                FetchedAtUtc = DateTime.UtcNow
            };

            _db.ExchangeRates.Add(rate);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("BCV rate saved for {Date} — USD={Usd} EUR={Eur}",
                today.ToString("yyyy-MM-dd"), usd, eur);

            return rate;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch BCV rates for {Date}", today.ToString("yyyy-MM-dd"));
            return null;
        }
    }

    internal async Task<(decimal usd, decimal eur)> ScrapeBcvAsync(CancellationToken ct)
    {
        _http.DefaultRequestHeaders.Clear();
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

        var html = await _http.GetStringAsync(BcvUrl, ct);

        var usd = ExtractRate(html, "dolar");
        var eur = ExtractRate(html, "euro");

        _logger.LogInformation("BCV scrape raw — USD={Usd} EUR={Eur}", usd, eur);

        return (usd, eur);
    }

    /// <summary>
    /// BCV homepage has divs with id like "dolar" or "euro" containing the rate.
    /// The rate appears as e.g. "36,81920000" inside a <strong> tag within that section.
    /// </summary>
    internal static decimal ExtractRate(string html, string currencyId)
    {
        // Pattern: find the div with the currency id, then extract the rate value
        // BCV format: <div id="dolar">...<strong>36,81920000</strong>...</div>
        // Also handles: <div class="...dolar...">...<strong>36,81920000</strong>...</div>
        var pattern = $@"id\s*=\s*""{currencyId}""[^>]*>[\s\S]{{0,2000}}?<strong[^>]*>\s*([\d,.]+)\s*</strong>";
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            // Fallback: look for class-based pattern
            pattern = $@"class\s*=\s*""[^""]*{currencyId}[^""]*""[^>]*>[\s\S]{{0,2000}}?<strong[^>]*>\s*([\d,.]+)\s*</strong>";
            match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        }

        if (!match.Success)
            return 0m;

        var raw = match.Groups[1].Value.Trim();

        // BCV uses comma as decimal separator: "36,81920000"
        raw = raw.Replace(".", "").Replace(",", ".");

        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate)
            ? Math.Round(rate, 2)
            : 0m;
    }
}
