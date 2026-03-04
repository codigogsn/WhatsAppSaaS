using Microsoft.AspNetCore.Mvc;
using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Api.Controllers;

[ApiController]
[Route("api/admin/analytics")]
public sealed class AdminAnalyticsController : ControllerBase
{
    private readonly IAdminAnalyticsService _svc;

    public AdminAnalyticsController(IAdminAnalyticsService svc)
    {
        _svc = svc;
    }

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var res = await _svc.GetSummaryAsync(ct);
        return Ok(res);
    }

    [HttpGet("top-products")]
    public async Task<IActionResult> TopProducts([FromQuery] int take = 10, CancellationToken ct = default)
    {
        var res = await _svc.GetTopProductsAsync(take, ct);
        return Ok(res);
    }

    [HttpGet("customers")]
    public async Task<IActionResult> Customers([FromQuery] int take = 50, CancellationToken ct = default)
    {
        var res = await _svc.GetCustomersAsync(take, ct);
        return Ok(res);
    }
}
