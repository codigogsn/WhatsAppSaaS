using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace Api.Controllers;

[ApiController]
[Route("admin/analytics")]
public class AdminAnalyticsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IAdminAnalyticsService _analytics;

    public AdminAnalyticsController(AppDbContext db, IAdminAnalyticsService analytics)
    {
        _db = db;
        _analytics = analytics;
    }

    // ── Auth helper: validates X-Admin-Key against the business's AdminKey ──
    private async Task<Guid?> AuthorizeBusinessAsync(Guid businessId, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Admin-Key", out var headerKey) || string.IsNullOrWhiteSpace(headerKey))
            return null;

        var biz = await _db.Businesses
            .AsNoTracking()
            .Where(b => b.Id == businessId && b.IsActive)
            .Select(b => new { b.Id, b.AdminKey })
            .FirstOrDefaultAsync(ct);

        if (biz is null || biz.AdminKey != headerKey.ToString())
            return null;

        return biz.Id;
    }

    // ── Legacy summary (kept for backward compat, route unchanged) ──
    [HttpGet("/api/admin/analytics/summary")]
    public async Task<IActionResult> GetSummary(CancellationToken ct)
    {
        var result = await _analytics.GetSummaryAsync(ct);
        return Ok(result);
    }

    // ── 1) Sales Analytics ──
    [HttpGet("sales")]
    public async Task<IActionResult> GetSales([FromQuery] Guid businessId, CancellationToken ct)
    {
        var bizId = await AuthorizeBusinessAsync(businessId, ct);
        if (bizId is null) return Unauthorized();

        var result = await _analytics.GetSalesAsync(bizId.Value, ct);
        return Ok(result);
    }

    // ── 2) Product Analytics ──
    [HttpGet("products")]
    public async Task<IActionResult> GetProducts([FromQuery] Guid businessId, CancellationToken ct)
    {
        var bizId = await AuthorizeBusinessAsync(businessId, ct);
        if (bizId is null) return Unauthorized();

        var result = await _analytics.GetProductAnalyticsAsync(bizId.Value, ct);
        return Ok(result);
    }

    // ── 3) Conversion Analytics ──
    [HttpGet("conversion")]
    public async Task<IActionResult> GetConversion([FromQuery] Guid businessId, CancellationToken ct)
    {
        var bizId = await AuthorizeBusinessAsync(businessId, ct);
        if (bizId is null) return Unauthorized();

        var result = await _analytics.GetConversionAsync(bizId.Value, ct);
        return Ok(result);
    }

    // ── 4) Operational Analytics ──
    [HttpGet("operations")]
    public async Task<IActionResult> GetOperations([FromQuery] Guid businessId, CancellationToken ct)
    {
        var bizId = await AuthorizeBusinessAsync(businessId, ct);
        if (bizId is null) return Unauthorized();

        var result = await _analytics.GetOperationalAsync(bizId.Value, ct);
        return Ok(result);
    }

    // ── 5) Business Intelligence ──
    [HttpGet("business")]
    public async Task<IActionResult> GetBusinessIntelligence([FromQuery] Guid businessId, CancellationToken ct)
    {
        var bizId = await AuthorizeBusinessAsync(businessId, ct);
        if (bizId is null) return Unauthorized();

        var result = await _analytics.GetBusinessIntelligenceAsync(bizId.Value, ct);
        return Ok(result);
    }

    // ── 6) Restaurant Insights ──
    [HttpGet("restaurant")]
    public async Task<IActionResult> GetRestaurantInsights([FromQuery] Guid businessId, CancellationToken ct)
    {
        var bizId = await AuthorizeBusinessAsync(businessId, ct);
        if (bizId is null) return Unauthorized();

        var result = await _analytics.GetRestaurantInsightsAsync(bizId.Value, ct);
        return Ok(result);
    }
}
