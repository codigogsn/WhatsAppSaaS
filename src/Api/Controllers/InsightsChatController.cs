using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Api.Controllers;

[ApiController]
[Route("api/insights/chat")]
[Authorize]
[EnableRateLimiting("admin")]
public sealed class InsightsChatController : ControllerBase
{
    private readonly IInsightsChatService _chat;
    private readonly ILogger<InsightsChatController> _logger;

    public InsightsChatController(IInsightsChatService chat, ILogger<InsightsChatController> logger)
    {
        _chat = chat;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Ask([FromBody] InsightsChatRequest request, CancellationToken ct)
    {
        var role = User.FindFirstValue(ClaimTypes.Role);

        // Founder scope requires Founder role
        if (request.Scope == "founder" && role != "Founder")
            return StatusCode(403, new { error = "Founder role required for global scope" });

        // Business scope requires Owner or Manager (or Founder)
        if (request.Scope != "founder" && role is not ("Founder" or "Owner" or "Manager"))
            return StatusCode(403, new { error = "Owner or Manager role required" });

        if (string.IsNullOrWhiteSpace(request.Question) || request.Question.Length > 500)
            return BadRequest(new { error = "Question is required (max 500 chars)" });

        var bizClaim = User.FindFirstValue("businessId");
        Guid? businessId = Guid.TryParse(bizClaim, out var bid) ? bid : null;

        try
        {
            var result = await _chat.AskAsync(request, businessId, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Insights chat error");
            return StatusCode(500, new { error = "Unexpected server error" });
        }
    }
}
