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
        => Ok(await _analytics.GetSummaryAsync(ct));

    [HttpGet("top-products")]
    public async Task<IActionResult> TopProducts([FromQuery] int take = 10, CancellationToken ct = default)
        => Ok(await _analytics.GetTopProductsAsync(take, ct));

    [HttpGet("customers")]
    public async Task<IActionResult> Customers([FromQuery] int take = 50, CancellationToken ct = default)
        => Ok(await _analytics.GetCustomersAsync(take, ct));
}
