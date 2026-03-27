using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using WhatsAppSaaS.Application.DTOs;
using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Api.Controllers;

/// <summary>
/// Test endpoint to exercise the AI parser without needing a WhatsApp webhook.
/// curl -X POST http://localhost:5000/ai/parse \
///   -H "Content-Type: application/json" \
///   -d '{"message":"quiero 2 hamburguesas para llevar","from":"5215512345678","conversation_id":"test-123"}'
/// </summary>
[ApiController]
[Route("ai")]
[EnableRateLimiting("admin")]
public sealed class AiController : ControllerBase
{
    private readonly IAiParser _aiParser;
    private readonly ILogger<AiController> _logger;

    public AiController(IAiParser aiParser, ILogger<AiController> logger)
    {
        _aiParser = aiParser;
        _logger = logger;
    }

    [HttpPost("parse")]
    public async Task<IActionResult> Parse(
        [FromBody] AiParseRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "message is required" });

        var conversationId = !string.IsNullOrWhiteSpace(request.ConversationId)
            ? request.ConversationId
            : Guid.NewGuid().ToString("N");

        var from = !string.IsNullOrWhiteSpace(request.From)
            ? request.From
            : "test-user";

        _logger.LogInformation(
            "AI parse test: message={Message}, from={From}, conversation={ConversationId}",
            request.Message, from, conversationId);

        var result = await _aiParser.ParseAsync(
            request.Message,
            from,
            conversationId,
            cancellationToken);

        return Ok(result);
    }
}
