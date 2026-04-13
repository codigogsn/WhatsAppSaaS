using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace Api.Controllers;

[ApiController]
[Route("api/admin/exchange-rates")]
[Authorize]
[EnableRateLimiting("admin")]
public class AdminExchangeRateController : ControllerBase
{
    private readonly AppDbContext _db;

    public AdminExchangeRateController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("current")]
    public async Task<IActionResult> GetCurrent(CancellationToken ct)
    {
        var today = DateTime.UtcNow.Date;

        var rate = await _db.ExchangeRates
            .Where(r => r.RateDate == today)
            .FirstOrDefaultAsync(ct);

        var isStale = false;

        if (rate is null)
        {
            rate = await _db.ExchangeRates
                .OrderByDescending(r => r.RateDate)
                .FirstOrDefaultAsync(ct);
            isStale = rate is not null;
        }

        if (rate is null)
            return Ok(new { available = false });

        return Ok(new
        {
            available = true,
            usdRate = rate.UsdRate,
            eurRate = rate.EurRate,
            rateDate = rate.RateDate.ToString("yyyy-MM-dd"),
            fetchedAtUtc = rate.FetchedAtUtc,
            source = rate.Source,
            isStale
        });
    }
}
