using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace Api.Controllers;

[ApiController]
[Route("api/admin/handoffs")]
[Authorize]
[EnableRateLimiting("admin")]
public class AdminHandoffsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly IWhatsAppClient _whatsAppClient;
    private readonly ILogger<AdminHandoffsController> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public AdminHandoffsController(AppDbContext db, IConfiguration config, IWhatsAppClient whatsAppClient, ILogger<AdminHandoffsController> logger)
    {
        _db = db;
        _config = config;
        _whatsAppClient = whatsAppClient;
        _logger = logger;
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
            _logger.LogError(ex, "Admin handoffs endpoint error");
            return StatusCode(500, new { error = "Unexpected server error" });
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

        // Append to chat log for transcript display
        var chatLog = new List<object>();
        if (dict.TryGetValue("humanChatLog", out var existing) && existing is JsonElement el && el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                chatLog.Add(item);
        }
        chatLog.Add(new { sender = "operator", text = req.Message, at = DateTime.UtcNow });
        // Cap at 50 messages to prevent state bloat
        if (chatLog.Count > 50) chatLog = chatLog.Skip(chatLog.Count - 50).ToList();
        dict["humanChatLog"] = chatLog;

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
            dict["humanHandoffRequested"] = false;
            dict["humanHandoffAtUtc"] = null;
            dict["humanHandoffNotifiedCount"] = 0;
            // Clear visible transcript only — structured state (items, cart, etc.) preserved
            dict["humanChatLog"] = new List<object>();

            entity.StateJson = JsonSerializer.Serialize(dict);
            entity.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            // Log preserved state
            var itemCount = 0;
            if (dict.TryGetValue("items", out var itemsObj) && itemsObj is JsonElement itemsEl && itemsEl.ValueKind == JsonValueKind.Array)
                itemCount = itemsEl.GetArrayLength();
            _logger.LogInformation("HANDOFF SESSION RESET: conversation={ConversationId} statePreserved=true items={Items}",
                conversationId, itemCount);

            // Send WhatsApp notification to customer
            var parts = conversationId.Split(':');
            if (parts.Length >= 2)
            {
                var customerPhone = parts[0];
                var phoneNumberId = parts[1];
                var accessToken = await _db.Businesses.AsNoTracking()
                    .Where(b => b.PhoneNumberId == phoneNumberId)
                    .Select(b => b.AccessToken)
                    .FirstOrDefaultAsync(ct)
                    ?? Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN");

                try
                {
                    await _whatsAppClient.SendTextMessageAsync(new OutgoingMessage
                    {
                        To = customerPhone,
                        PhoneNumberId = phoneNumberId,
                        AccessToken = accessToken,
                        Body = "Tu conversaci\u00f3n fue devuelta al asistente autom\u00e1tico \ud83e\udd16. Puedes seguir escribiendo por aqu\u00ed y con gusto te ayudo."
                    }, ct);
                    _logger.LogInformation("RETURN TO BOT: outbound message sent to {Phone}", customerPhone);
                }
                catch (Exception msgEx)
                {
                    _logger.LogWarning(msgEx, "RETURN TO BOT: failed to send WhatsApp notification to {Phone}", customerPhone);
                }
            }

            return Ok(new { returnedToBot = true, conversationId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin handoffs endpoint error");
            return StatusCode(500, new { error = "Unexpected server error" });
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

                // Extract chat log for transcript. Media metadata (kind/mediaId/mimeType)
                // is optional — older entries written before the schema extension simply
                // get null/"text" defaults so the frontend renders them as plain text.
                var chatLog = new List<object>();
                if (root.TryGetProperty("humanChatLog", out var clProp) && clProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in clProp.EnumerateArray())
                    {
                        chatLog.Add(new
                        {
                            sender = entry.TryGetProperty("sender", out var sp) ? sp.GetString() : "unknown",
                            text = entry.TryGetProperty("text", out var tp) ? tp.GetString() : "",
                            at = entry.TryGetProperty("at", out var ap) && ap.ValueKind == JsonValueKind.String
                                ? ap.GetString() : null,
                            kind = entry.TryGetProperty("kind", out var kp) && kp.ValueKind == JsonValueKind.String
                                ? kp.GetString() : "text",
                            mediaId = entry.TryGetProperty("mediaId", out var mp) && mp.ValueKind == JsonValueKind.String
                                ? mp.GetString() : null,
                            mimeType = entry.TryGetProperty("mimeType", out var mtp) && mtp.ValueKind == JsonValueKind.String
                                ? mtp.GetString() : null
                        });
                    }
                }

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
                    lastMessageAt = s.UpdatedAtUtc,
                    chatLog
                });
            }
            catch { /* skip malformed JSON */ }
        }

        return Ok(new { total = conversations.Count, conversations });
    }

    /// <summary>
    /// GET /api/admin/handoffs/{conversationId}/media/{mediaId}
    /// Streams an inbound WhatsApp media attachment (image/document) so the
    /// handoff console can render thumbnails / lightbox previews. Mirrors the
    /// security model + disk cache of OrdersController.GetPaymentProof but
    /// keyed on the conversation, since handoff media may exist before any
    /// Order row is created.
    /// </summary>
    [HttpGet("{conversationId}/media/{mediaId}")]
    public async Task<IActionResult> GetMedia(string conversationId, string mediaId, CancellationToken ct)
    {
        if (!IsAuthorized())
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(mediaId))
            return BadRequest(new { error = "mediaId is required" });

        var entity = await _db.ConversationStates.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ConversationId == conversationId, ct);
        if (entity is null)
            return NotFound(new { error = "Conversation not found" });
        if (!IsAuthorizedForConversation(entity))
            return Unauthorized(new { error = "Not authorized for this conversation's business" });

        // Verify the mediaId actually appears in this conversation's chatLog
        // OR matches the conversation's PaymentProofMediaId. Prevents using
        // this endpoint as an arbitrary WhatsApp media fetcher.
        var (knownMime, isReferenced) = ScanChatLogForMedia(entity.StateJson, mediaId);
        if (!isReferenced)
            return NotFound(new { error = "Media not referenced by this conversation" });

        // ── Local cache: serve from disk if previously downloaded ──
        var (cachedData, cachedType) = await ReadMediaCacheAsync(mediaId, ct);
        if (cachedData is not null)
        {
            Response.Headers["Cache-Control"] = "private, max-age=300";
            return File(cachedData, SafeMediaContentType(cachedType!));
        }

        // Resolve per-business token. Fail closed — never fall back to a
        // global token for tenant-bound media.
        if (!entity.BusinessId.HasValue)
        {
            _logger.LogError("GetMedia: conversation {ConversationId} has no BusinessId", conversationId);
            return StatusCode(500, new { error = "No se pudo verificar la cuenta del negocio para esta conversación." });
        }

        string? bizToken = null;
        try
        {
            bizToken = await _db.Businesses.AsNoTracking()
                .Where(b => b.Id == entity.BusinessId.Value && b.IsActive)
                .Select(b => b.AccessToken)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "GetMedia: failed to look up business {BusinessId} for conversation {ConversationId}",
                entity.BusinessId, conversationId);
            return StatusCode(502, new { error = "No se pudo obtener las credenciales del negocio. Intenta de nuevo." });
        }

        if (string.IsNullOrWhiteSpace(bizToken))
        {
            _logger.LogError(
                "GetMedia: no access token for business {BusinessId} (conversation {ConversationId})",
                entity.BusinessId, conversationId);
            return StatusCode(502, new { error = "El negocio no tiene token de acceso configurado." });
        }

        var result = await _whatsAppClient.GetMediaAsync(mediaId, bizToken, ct);
        if (result is null)
        {
            _logger.LogWarning(
                "GetMedia: download returned null for mediaId={MediaId} conversation={ConversationId}",
                mediaId, conversationId);
            return StatusCode(502, new { error = "No se pudo descargar el archivo desde WhatsApp." });
        }

        await WriteMediaCacheAsync(mediaId, result.Data, result.ContentType, ct);

        var contentType = SafeMediaContentType(result.ContentType ?? knownMime ?? "application/octet-stream");
        Response.Headers["Cache-Control"] = "private, max-age=300";
        return File(result.Data, contentType);
    }

    // ── Handoff media disk cache (mirrors OrdersController proof cache pattern) ──

    private static readonly string MediaCacheDir =
        Path.Combine(AppContext.BaseDirectory, "data", "handoff_media");

    private static string MediaBinPath(string mediaId) =>
        Path.Combine(MediaCacheDir, SafeFileToken(mediaId) + ".bin");

    private static string MediaTypePath(string mediaId) =>
        Path.Combine(MediaCacheDir, SafeFileToken(mediaId) + ".type");

    private static string SafeFileToken(string mediaId)
    {
        // WhatsApp media ids are numeric strings, but defensive sanitisation
        // keeps this safe against any future format change.
        var sb = new StringBuilder(mediaId.Length);
        foreach (var ch in mediaId)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        return sb.ToString();
    }

    private static async Task<(byte[]? data, string? contentType)> ReadMediaCacheAsync(string mediaId, CancellationToken ct)
    {
        var binPath = MediaBinPath(mediaId);
        var typePath = MediaTypePath(mediaId);
        if (!System.IO.File.Exists(binPath) || !System.IO.File.Exists(typePath))
            return (null, null);
        try
        {
            var data = await System.IO.File.ReadAllBytesAsync(binPath, ct);
            var contentType = (await System.IO.File.ReadAllTextAsync(typePath, ct)).Trim();
            if (data.Length == 0 || string.IsNullOrWhiteSpace(contentType))
                return (null, null);
            return (data, contentType);
        }
        catch
        {
            return (null, null);
        }
    }

    private async Task WriteMediaCacheAsync(string mediaId, byte[] data, string contentType, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(MediaCacheDir);
            await System.IO.File.WriteAllBytesAsync(MediaBinPath(mediaId), data, ct);
            await System.IO.File.WriteAllTextAsync(MediaTypePath(mediaId), contentType ?? "application/octet-stream", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache handoff media for mediaId={MediaId}", mediaId);
        }
    }

    private static readonly HashSet<string> AllowedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "image/gif", "application/pdf"
    };

    private static string SafeMediaContentType(string ct) =>
        AllowedMediaTypes.Contains(ct) ? ct : "application/octet-stream";

    /// <summary>
    /// Scans a conversation's StateJson for any HumanChatLog entry referencing
    /// the given mediaId, OR for a top-level PaymentProofMediaId match. Returns
    /// the recorded MIME type (if any) and whether the reference was found.
    /// </summary>
    private static (string? mimeType, bool isReferenced) ScanChatLogForMedia(string? stateJson, string mediaId)
    {
        if (string.IsNullOrWhiteSpace(stateJson)) return (null, false);
        try
        {
            using var doc = JsonDocument.Parse(stateJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("paymentProofMediaId", out var ppm)
                && ppm.ValueKind == JsonValueKind.String
                && string.Equals(ppm.GetString(), mediaId, StringComparison.Ordinal))
            {
                return (null, true);
            }

            if (root.TryGetProperty("humanChatLog", out var cl) && cl.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in cl.EnumerateArray())
                {
                    if (entry.TryGetProperty("mediaId", out var mp)
                        && mp.ValueKind == JsonValueKind.String
                        && string.Equals(mp.GetString(), mediaId, StringComparison.Ordinal))
                    {
                        var mime = entry.TryGetProperty("mimeType", out var mtp) && mtp.ValueKind == JsonValueKind.String
                            ? mtp.GetString()
                            : null;
                        return (mime, true);
                    }
                }
            }
        }
        catch { /* malformed JSON ⇒ treat as not referenced */ }

        return (null, false);
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
