using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Api.Controllers;

[ApiController]
[Route("api/founder/insights")]
[Authorize]
[EnableRateLimiting("admin")]
public sealed class FounderInsightsController : ControllerBase
{
    private readonly IFounderInsightsService _insights;
    private readonly ILogger<FounderInsightsController> _logger;

    public FounderInsightsController(IFounderInsightsService insights, ILogger<FounderInsightsController> logger)
    {
        _insights = insights;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var role = User.FindFirstValue(ClaimTypes.Role);
        if (role != "Founder")
            return StatusCode(403, new { error = "Founder role required" });

        try
        {
            var result = await _insights.GetInsightsAsync(ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Founder insights endpoint error");
            return StatusCode(500, new { error = "Unexpected server error" });
        }
    }
}
