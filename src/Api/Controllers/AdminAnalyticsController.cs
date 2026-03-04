using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Api.Controllers;

[ApiController]
[Route("api/admin/analytics")]
public sealed class AdminAnalyticsController : ControllerBase
{
    private readonly IAdminAnalyticsService _analytics;

    public AdminAnalyticsController(IAdminAnalyticsService analytics)
    {
        _analytics = analytics;
    }

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var result = await _analytics.GetSummaryAsync(ct);
        return Ok(result);
    }

    [HttpGet("top-products")]
    public async Task<IActionResult> TopProducts([FromQuery] int take = 10, CancellationToken ct = default)
    {
        var result = await _analytics.GetTopProductsAsync(take, ct);
        return Ok(result);
    }

    [HttpGet("customers")]
    public async Task<IActionResult> Customers([FromQuery] int take = 50, CancellationToken ct = default)
    {
        var result = await _analytics.GetCustomersAsync(take, ct);
        return Ok(result);
    }
}
