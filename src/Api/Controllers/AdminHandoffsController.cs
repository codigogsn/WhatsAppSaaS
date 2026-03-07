using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace Api.Controllers;

[ApiController]
[Route("api/admin/handoffs")]
public class AdminHandoffsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public AdminHandoffsController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    private bool IsAuthorized()
    {
        if (!Request.Headers.TryGetValue("X-Admin-Key", out var headerKey) || string.IsNullOrWhiteSpace(headerKey))
            return false;

        var globalKey = _config["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY");
        if (!string.IsNullOrWhiteSpace(globalKey) && headerKey.ToString() == globalKey)
            return true;

        // Also accept per-business admin keys
        var bizKey = _db.Businesses.AsNoTracking().Any(b => b.AdminKey == headerKey.ToString() && b.IsActive);
        return bizKey;
    }

    /// <summary>
    /// GET /api/admin/handoffs?businessId=xxx
    /// Returns conversations currently in human-handoff state.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? businessId, CancellationToken ct)
    {
        if (!IsAuthorized())
            return Unauthorized();

        var q = _db.ConversationStates.AsNoTracking().AsQueryable();

        if (businessId.HasValue)
            q = q.Where(s => s.BusinessId == businessId.Value);

        // Load all states and filter in-memory for JSON field
        var states = await q.ToListAsync(ct);

        var handoffs = new List<object>();
        foreach (var s in states)
        {
            try
            {
                using var doc = JsonDocument.Parse(s.StateJson ?? "{}");
                var root = doc.RootElement;

                if (root.TryGetProperty("humanHandoffRequested", out var prop) && prop.GetBoolean())
                {
                    // Extract the "from" phone from conversationId format "from:phoneNumberId"
                    var parts = s.ConversationId.Split(':');
                    var fromPhone = parts.Length > 0 ? parts[0] : s.ConversationId;
                    var phoneNumberId = parts.Length > 1 ? parts[1] : "";

                    DateTime? handoffAt = null;
                    if (root.TryGetProperty("humanHandoffAtUtc", out var atProp) && atProp.ValueKind == JsonValueKind.String)
                        handoffAt = DateTime.TryParse(atProp.GetString(), out var dt) ? dt : null;

                    int notifiedCount = 0;
                    if (root.TryGetProperty("humanHandoffNotifiedCount", out var ncProp))
                        notifiedCount = ncProp.GetInt32();

                    handoffs.Add(new
                    {
                        conversationId = s.ConversationId,
                        fromPhone,
                        phoneNumberId,
                        businessId = s.BusinessId,
                        handoffAtUtc = handoffAt,
                        notifiedCount,
                        updatedAtUtc = s.UpdatedAtUtc
                    });
                }
            }
            catch { /* skip malformed JSON */ }
        }

        return Ok(handoffs);
    }

    /// <summary>
    /// POST /api/admin/handoffs/{conversationId}/resolve
    /// Clears the human-handoff flag so the bot resumes.
    /// </summary>
    [HttpPost("{conversationId}/resolve")]
    public async Task<IActionResult> Resolve(string conversationId, CancellationToken ct)
    {
        if (!IsAuthorized())
            return Unauthorized();

        var entity = await _db.ConversationStates.FindAsync(new object[] { conversationId }, ct);
        if (entity is null)
            return NotFound(new { error = "Conversation not found" });

        try
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(entity.StateJson ?? "{}", JsonOpts)
                         ?? new Dictionary<string, JsonElement>();

            // Create a mutable copy using JsonNode
            using var doc = JsonDocument.Parse(entity.StateJson ?? "{}");
            var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(entity.StateJson ?? "{}");
            if (dict is null) dict = new();

            dict["humanHandoffRequested"] = false;
            dict["humanHandoffAtUtc"] = null;
            dict["humanHandoffNotifiedCount"] = 0;

            entity.StateJson = JsonSerializer.Serialize(dict);
            entity.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            return Ok(new { resolved = true, conversationId });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
