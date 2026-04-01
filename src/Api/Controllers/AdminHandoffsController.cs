using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace Api.Controllers;

[ApiController]
[Route("api/admin/handoffs")]
[EnableRateLimiting("admin")]
public class AdminHandoffsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly IWhatsAppClient _whatsAppClient;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public AdminHandoffsController(AppDbContext db, IConfiguration config, IWhatsAppClient whatsAppClient)
    {
        _db = db;
        _config = config;
        _whatsAppClient = whatsAppClient;
    }

    private Guid? GetJwtBusinessId()
    {
        var claim = User.FindFirstValue("businessId");
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private bool IsAuthorized()
    {
        // JWT auth: any staff role
        var role = User.FindFirstValue(ClaimTypes.Role);
        if (role is "Founder" or "Owner" or "Manager" or "Operator")
            return true;

        if (!Request.Headers.TryGetValue("X-Admin-Key", out var headerKey) || string.IsNullOrWhiteSpace(headerKey))
            return false;

        var globalKey = _config["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY");
        if (!string.IsNullOrWhiteSpace(globalKey) && ConstantTimeEquals(headerKey.ToString(), globalKey))
            return true;

        // Also accept per-business admin keys (fetch then constant-time compare)
        var key = headerKey.ToString().Trim();
        var bizKeys = _db.Businesses.AsNoTracking()
            .Where(b => b.IsActive && b.AdminKey != null)
            .Select(b => b.AdminKey!)
            .ToList();
        return bizKeys.Any(bk => ConstantTimeEquals(key, bk));
    }

    /// <summary>
    /// Checks if the JWT user has access to the conversation's business.
    /// Global admin key holders bypass this check.
    /// </summary>
    private bool IsAuthorizedForConversation(ConversationState entity)
    {
        // Global admin key: unrestricted
        var globalKey = _config["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY");
        if (!string.IsNullOrWhiteSpace(globalKey)
            && Request.Headers.TryGetValue("X-Admin-Key", out var hk)
            && ConstantTimeEquals(hk.ToString(), globalKey))
            return true;

        // JWT user: must match conversation's business
        var jwtBizId = GetJwtBusinessId();
        if (!jwtBizId.HasValue) return false;
        return entity.BusinessId.HasValue && entity.BusinessId.Value == jwtBizId.Value;
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

        // Founder can view any business; other JWT users are scoped to their own
        var jwtBizId = GetJwtBusinessId();
        var jwtRole = User.FindFirstValue(ClaimTypes.Role);
        if (jwtBizId.HasValue && jwtRole != "Founder")
            businessId = jwtBizId.Value;

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
        if (!IsAuthorizedForConversation(entity))
            return Unauthorized(new { error = "Not authorized for this conversation's business" });

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
            dict["humanOverride"] = false;
            dict["humanOverrideAtUtc"] = null;

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

    /// <summary>
    /// POST /api/admin/handoffs/{conversationId}/reply
    /// Sends a WhatsApp message from the admin and activates human override.
    /// </summary>
    [HttpPost("{conversationId}/reply")]
    public async Task<IActionResult> Reply(string conversationId, [FromBody] ReplyRequest req, CancellationToken ct)
    {
        if (!IsAuthorized())
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(req?.Message))
            return BadRequest(new { error = "Message is required" });

        var entity = await _db.ConversationStates.FindAsync(new object[] { conversationId }, ct);
        if (entity is null)
            return NotFound(new { error = "Conversation not found" });
        if (!IsAuthorizedForConversation(entity))
            return Unauthorized(new { error = "Not authorized for this conversation's business" });

        // Parse conversationId → "from:phoneNumberId"
        var parts = conversationId.Split(':');
        if (parts.Length < 2)
            return BadRequest(new { error = "Invalid conversationId format" });

        var customerPhone = parts[0];
        var phoneNumberId = parts[1];

        // Resolve only the access token by PhoneNumberId — projection avoids
        // materializing Business.Id which fails on legacy text-id schemas.
        var accessToken = await _db.Businesses.AsNoTracking()
            .Where(b => b.PhoneNumberId == phoneNumberId)
            .Select(b => b.AccessToken)
            .FirstOrDefaultAsync(ct)
            ?? Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN")
            ?? Environment.GetEnvironmentVariable("META_ACCESS_TOKEN");

        // Send WhatsApp message
        var sent = await _whatsAppClient.SendTextMessageAsync(new OutgoingMessage
        {
            To = customerPhone,
            Body = req.Message,
            PhoneNumberId = phoneNumberId,
            AccessToken = accessToken
        }, ct);

        if (!sent)
            return StatusCode(502, new { error = "Failed to send WhatsApp message" });

        // Activate human override
        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(entity.StateJson ?? "{}");
        if (dict is null) dict = new();

        dict["humanOverride"] = true;
        dict["humanOverrideAtUtc"] = DateTime.UtcNow;

        entity.StateJson = JsonSerializer.Serialize(dict);
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(new { sent = true, conversationId, to = customerPhone });
    }

    /// <summary>
    /// POST /api/admin/handoffs/{conversationId}/return-to-bot
    /// Clears human override so the bot resumes automated responses.
    /// </summary>
    [HttpPost("{conversationId}/return-to-bot")]
    public async Task<IActionResult> ReturnToBot(string conversationId, CancellationToken ct)
    {
        if (!IsAuthorized())
            return Unauthorized();

        var entity = await _db.ConversationStates.FindAsync(new object[] { conversationId }, ct);
        if (entity is null)
            return NotFound(new { error = "Conversation not found" });
        if (!IsAuthorizedForConversation(entity))
            return Unauthorized(new { error = "Not authorized for this conversation's business" });

        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(entity.StateJson ?? "{}");
            if (dict is null) dict = new();

            dict["humanOverride"] = false;
            dict["humanOverrideAtUtc"] = null;
            // Also clear customer-initiated handoff if present
            dict["humanHandoffRequested"] = false;
            dict["humanHandoffAtUtc"] = null;
            dict["humanHandoffNotifiedCount"] = 0;

            entity.StateJson = JsonSerializer.Serialize(dict);
            entity.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            return Ok(new { returnedToBot = true, conversationId });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/admin/handoffs/human?businessId=xxx
    /// Returns conversations currently under human override (admin active).
    /// </summary>
    [HttpGet("human")]
    public async Task<IActionResult> ListHumanOverride([FromQuery] Guid? businessId, CancellationToken ct)
    {
        if (!IsAuthorized())
            return Unauthorized();

        // Founder can view any business; other JWT users are scoped to their own
        var jwtBizId = GetJwtBusinessId();
        var jwtRole = User.FindFirstValue(ClaimTypes.Role);
        if (jwtBizId.HasValue && jwtRole != "Founder")
            businessId = jwtBizId.Value;

        var q = _db.ConversationStates.AsNoTracking().AsQueryable();

        if (businessId.HasValue)
            q = q.Where(s => s.BusinessId == businessId.Value);

        var states = await q.ToListAsync(ct);

        var conversations = new List<object>();
        foreach (var s in states)
        {
            try
            {
                using var doc = JsonDocument.Parse(s.StateJson ?? "{}");
                var root = doc.RootElement;

                // Include if humanOverride OR humanHandoffRequested is true
                var isHumanOverride = root.TryGetProperty("humanOverride", out var ovProp)
                    && ovProp.ValueKind == JsonValueKind.True;
                var isHandoffRequested = root.TryGetProperty("humanHandoffRequested", out var hrProp)
                    && hrProp.ValueKind == JsonValueKind.True;

                if (!isHumanOverride && !isHandoffRequested)
                    continue;

                var parts = s.ConversationId.Split(':');
                var fromPhone = parts.Length > 0 ? parts[0] : s.ConversationId;
                var phoneNumberId = parts.Length > 1 ? parts[1] : "";

                string? customerName = null;
                if (root.TryGetProperty("customerName", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
                    customerName = nameProp.GetString();

                DateTime? overrideAt = null;
                if (root.TryGetProperty("humanOverrideAtUtc", out var oaProp) && oaProp.ValueKind == JsonValueKind.String)
                    overrideAt = DateTime.TryParse(oaProp.GetString(), out var dt) ? dt : null;

                DateTime? handoffAt = null;
                if (root.TryGetProperty("humanHandoffAtUtc", out var haProp) && haProp.ValueKind == JsonValueKind.String)
                    handoffAt = DateTime.TryParse(haProp.GetString(), out var dt2) ? dt2 : null;

                conversations.Add(new
                {
                    conversationId = s.ConversationId,
                    phone = fromPhone,
                    phoneNumberId,
                    customerName = customerName ?? "N/A",
                    businessId = s.BusinessId,
                    humanOverride = isHumanOverride,
                    humanOverrideAtUtc = overrideAt,
                    humanHandoffRequested = isHandoffRequested,
                    humanHandoffAtUtc = handoffAt,
                    lastMessageAt = s.UpdatedAtUtc
                });
            }
            catch { /* skip malformed JSON */ }
        }

        return Ok(new { total = conversations.Count, conversations });
    }

    public sealed class ReplyRequest
    {
        public string? Message { get; set; }
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b));
    }
}
