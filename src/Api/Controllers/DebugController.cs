using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace Api.Controllers;

[ApiController]
[Route("debug")]
[EnableRateLimiting("admin")]
public class DebugController : ControllerBase
{
    [HttpGet("version")]
    public IActionResult Version([FromServices] IHostEnvironment hostEnv)
    {
        if (!hostEnv.IsDevelopment())
            return NotFound();

        var sha = Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT")
                  ?? Environment.GetEnvironmentVariable("GIT_COMMIT")
                  ?? "unknown";

        var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                  ?? "unknown";

        return Ok(new
        {
            commit = sha,
            aspnetcore_environment = env,
            utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
        });
    }

    [HttpGet("webhook-state")]
    public async Task<IActionResult> WebhookState(
        [FromQuery] string? from,
        [FromQuery] string? phoneNumberId,
        [FromServices] IHostEnvironment hostEnv,
        [FromServices] AppDbContext db,
        CancellationToken ct)
    {
        if (!hostEnv.IsDevelopment())
            return NotFound();

        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(phoneNumberId))
            return BadRequest("Provide ?from=...&phoneNumberId=...");

        var conversationId = $"{from}:{phoneNumberId}";

        var state = await db.ConversationStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ConversationId == conversationId, ct);

        var processedCount = await db.ProcessedMessages
            .CountAsync(p => p.ConversationId == conversationId, ct);

        var recentMessages = await db.ProcessedMessages
            .Where(p => p.ConversationId == conversationId)
            .OrderByDescending(p => p.CreatedAtUtc)
            .Take(10)
            .Select(p => new { p.MessageId, p.CreatedAtUtc })
            .ToListAsync(ct);

        return Ok(new
        {
            conversationId,
            found = state is not null,
            updatedAtUtc = state?.UpdatedAtUtc,
            businessId = state?.BusinessId,
            stateJson = state?.StateJson,
            processedMessagesCount = processedCount,
            recentMessages
        });
    }
}
