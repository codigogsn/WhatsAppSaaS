using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Api.Controllers;

[ApiController]
[Route("api/insights")]
[Authorize]
[EnableRateLimiting("admin")]
public sealed class InsightsController : ControllerBase
{
    private readonly IBusinessInsightsService _insights;
    private readonly ILogger<InsightsController> _logger;

    public InsightsController(IBusinessInsightsService insights, ILogger<InsightsController> logger)
    {
        _insights = insights;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/insights?windowDays=30
    /// Returns business intelligence insights for the authenticated user's business.
    /// Restricted to Owner and Manager roles.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetInsights([FromQuery] int windowDays = 30, CancellationToken ct = default)
    {
        // Role check: Owner or Manager only (Founder also allowed)
        var role = User.FindFirstValue(ClaimTypes.Role);
        if (role is not ("Founder" or "Owner" or "Manager"))
            return StatusCode(403, new { error = "Insufficient role. Owner or Manager required." });

        // Business scope from JWT
        var bizClaim = User.FindFirstValue("businessId");
        if (!Guid.TryParse(bizClaim, out var businessId))
            return BadRequest(new { error = "No business context found in token." });

        // Clamp window
        if (windowDays < 7) windowDays = 7;
        if (windowDays > 90) windowDays = 90;

        try
        {
            var result = await _insights.GetInsightsAsync(businessId, windowDays, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Insights endpoint error for business {BusinessId}", businessId);
            return StatusCode(500, new { error = "Unexpected server error" });
        }
    }
}
