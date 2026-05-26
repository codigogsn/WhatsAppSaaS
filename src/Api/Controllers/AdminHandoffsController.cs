using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Application.Services;
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
    private readonly IExchangeRateProvider _exchangeRateProvider;
    private readonly IOrderRepository _orderRepository;
    private readonly INotificationService _notificationService;
    private readonly ILogger<AdminHandoffsController> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public AdminHandoffsController(
        AppDbContext db,
        IConfiguration config,
        IWhatsAppClient whatsAppClient,
        IExchangeRateProvider exchangeRateProvider,
        IOrderRepository orderRepository,
        INotificationService notificationService,
        ILogger<AdminHandoffsController> logger)
    {
        _db = db;
        _config = config;
        _whatsAppClient = whatsAppClient;
        _exchangeRateProvider = exchangeRateProvider;
        _orderRepository = orderRepository;
        _notificationService = notificationService;
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

                // Optional commit-1 surfaces — only present when set on the state.
                // Frontend doesn't consume yet; the API contract is published now so
                // later commits can iterate UI without another backend deploy.
                OperatorDraft? operatorDraft = null;
                if (root.TryGetProperty("operatorDraft", out var odProp) && odProp.ValueKind == JsonValueKind.Object)
                {
                    try { operatorDraft = odProp.Deserialize<OperatorDraft>(JsonOpts); }
                    catch { /* malformed draft — surface as null */ }
                }
                Guid? orderCreatedByHumanId = null;
                if (root.TryGetProperty("orderCreatedByHumanId", out var ocProp)
                    && ocProp.ValueKind == JsonValueKind.String
                    && Guid.TryParse(ocProp.GetString(), out var ocGuid))
                {
                    orderCreatedByHumanId = ocGuid;
                }
                var humanInterventionResolved = root.TryGetProperty("humanInterventionResolved", out var hirProp)
                    && hirProp.ValueKind == JsonValueKind.True;

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
                    chatLog,
                    operatorDraft,
                    orderCreatedByHumanId,
                    humanInterventionResolved
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

    // ── Operator draft endpoints (Commit 1 of Human Order Capture) ──
    //
    // The operator's structured order draft lives on ConversationFields.OperatorDraft.
    // These endpoints expose CRUD only — order creation, payment side effects, and
    // bot mutations are out of scope for this commit and will land in later commits.

    /// <summary>
    /// GET /api/admin/handoffs/{conversationId}/draft
    /// Returns the current operator draft, or null when none exists.
    /// </summary>
    [HttpGet("{conversationId}/draft")]
    public async Task<IActionResult> GetDraft(string conversationId, CancellationToken ct)
    {
        if (!IsAuthorized()) return Unauthorized();
        var entity = await _db.ConversationStates.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ConversationId == conversationId, ct);
        if (entity is null) return NotFound(new { error = "Conversation not found" });
        if (!IsAuthorizedForConversation(entity))
            return Unauthorized(new { error = "Not authorized for this conversation's business" });

        return Ok(new { draft = ReadDraftFromStateJson(entity.StateJson) });
    }

    /// <summary>
    /// PUT /api/admin/handoffs/{conversationId}/draft
    /// Replaces the operator draft in full. The If-Match request header (ISO-8601
    /// timestamp of the expected current UpdatedAtUtc) gates concurrent edits —
    /// 412 on mismatch, with the current draft included so the UI can re-sync.
    /// </summary>
    [HttpPut("{conversationId}/draft")]
    public async Task<IActionResult> PutDraft(string conversationId, [FromBody] OperatorDraft draft, CancellationToken ct)
    {
        if (!IsAuthorized()) return Unauthorized();
        if (draft is null) return BadRequest(new { error = "Draft body is required" });

        var entity = await _db.ConversationStates.FindAsync(new object[] { conversationId }, ct);
        if (entity is null) return NotFound(new { error = "Conversation not found" });
        if (!IsAuthorizedForConversation(entity))
            return Unauthorized(new { error = "Not authorized for this conversation's business" });

        var (concurrencyError, currentDraft) = CheckDraftConcurrency(entity.StateJson);
        if (concurrencyError is not null)
            return StatusCode(StatusCodes.Status412PreconditionFailed,
                new { error = concurrencyError, current = currentDraft });

        draft.UpdatedAtUtc = DateTime.UtcNow;
        // Operator-initiated write transfers ownership away from the parser.
        // The intake parser will only fill empty fields after this point.
        draft.AutoFilledFromCustomer = false;
        await WriteDraftAsync(entity, draft, ct);
        return Ok(new { draft });
    }

    /// <summary>
    /// PATCH /api/admin/handoffs/{conversationId}/draft
    /// Merges the provided fields onto the current draft. Null-valued fields
    /// leave the existing value alone; non-null fields replace; Items is special-
    /// cased — null leaves the list alone, [] clears it, [..] replaces. Same
    /// If-Match semantics as PUT. Creates a new draft when none exists.
    /// </summary>
    [HttpPatch("{conversationId}/draft")]
    public async Task<IActionResult> PatchDraft(string conversationId, [FromBody] OperatorDraftPatchRequest patch, CancellationToken ct)
    {
        if (!IsAuthorized()) return Unauthorized();
        if (patch is null) return BadRequest(new { error = "Patch body is required" });

        var entity = await _db.ConversationStates.FindAsync(new object[] { conversationId }, ct);
        if (entity is null) return NotFound(new { error = "Conversation not found" });
        if (!IsAuthorizedForConversation(entity))
            return Unauthorized(new { error = "Not authorized for this conversation's business" });

        var (concurrencyError, currentDraft) = CheckDraftConcurrency(entity.StateJson);
        if (concurrencyError is not null)
            return StatusCode(StatusCodes.Status412PreconditionFailed,
                new { error = concurrencyError, current = currentDraft });

        var merged = MergeDraftPatch(currentDraft ?? new OperatorDraft(), patch);
        merged.UpdatedAtUtc = DateTime.UtcNow;
        // Operator-initiated PATCH transfers ownership away from the parser
        // even when the patch body itself doesn't touch a previously
        // parser-filled field — the act of editing implies operator intent.
        merged.AutoFilledFromCustomer = false;
        await WriteDraftAsync(entity, merged, ct);
        return Ok(new { draft = merged });
    }

    /// <summary>
    /// DELETE /api/admin/handoffs/{conversationId}/draft
    /// Clears the operator draft. Idempotent — succeeds even when none exists.
    /// </summary>
    [HttpDelete("{conversationId}/draft")]
    public async Task<IActionResult> DeleteDraft(string conversationId, CancellationToken ct)
    {
        if (!IsAuthorized()) return Unauthorized();
        var entity = await _db.ConversationStates.FindAsync(new object[] { conversationId }, ct);
        if (entity is null) return NotFound(new { error = "Conversation not found" });
        if (!IsAuthorizedForConversation(entity))
            return Unauthorized(new { error = "Not authorized for this conversation's business" });

        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(entity.StateJson ?? "{}", JsonOpts) ?? new();
        var removed = dict.Remove("operatorDraft");
        if (removed)
        {
            entity.StateJson = JsonSerializer.Serialize(dict, JsonOpts);
            entity.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return Ok(new { deleted = true, hadDraft = removed });
    }

    /// <summary>
    /// POST /api/admin/handoffs/{conversationId}/draft/preview-message
    /// Validates the operator draft and, when structurally valid, returns a
    /// composed customer-facing message (Spanish) using the existing bot-flow
    /// templates plus the per-business Pago-Móvil config and current BCV rate.
    /// Side-effect free — the message is NOT sent. The operator copies it into
    /// the composer and presses Enviar manually.
    /// </summary>
    [HttpPost("{conversationId}/draft/preview-message")]
    public async Task<IActionResult> PreviewDraftMessage(string conversationId, CancellationToken ct)
    {
        if (!IsAuthorized()) return Unauthorized();

        var entity = await _db.ConversationStates.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ConversationId == conversationId, ct);
        if (entity is null) return NotFound(new { error = "Conversation not found" });
        if (!IsAuthorizedForConversation(entity))
            return Unauthorized(new { error = "Not authorized for this conversation's business" });

        var eval = await EvaluateDraftAsync(entity, ct);
        if (eval is null)
            return UnprocessableEntity(new { ok = false, error = "No hay borrador para esta conversación.", missing = new[] { "draft" }, unpriced = Array.Empty<string>() });

        if (!eval.IsComplete)
        {
            return UnprocessableEntity(new
            {
                ok = false,
                error = "El borrador no está completo para generar el mensaje.",
                missing = eval.Missing,
                unpriced = eval.Unpriced
            });
        }

        // Compose the customer-facing message using the existing template
        // helpers. EvaluateDraftAsync already resolved biz + BCV.
        var draft = eval.Draft;
        var biz = eval.Business;

        var items = draft.Items!
            .Select(i => new ConversationItemEntry
            {
                Name = i.Name ?? "",
                Quantity = i.Quantity,
                Modifiers = i.Modifiers,
                UnitPrice = i.UnitPrice ?? 0m
            })
            .ToList();

        var input = new HandoffReviewInput
        {
            CustomerName        = draft.CustomerName!,
            CustomerIdNumber    = draft.CustomerIdNumber!,
            CustomerPhone       = draft.CustomerPhone!,
            Address             = draft.Address,
            DeliveryType        = draft.DeliveryType!,
            PaymentMethod       = draft.PaymentMethod!,
            SpecialInstructions = draft.SpecialInstructions,
            Items               = items,
            PagoMovilBank       = biz?.PaymentMobileBank,
            PagoMovilId         = biz?.PaymentMobileId,
            PagoMovilPhone      = biz?.PaymentMobilePhone
        };

        var message = HandoffMessageBuilder.Build(input, eval.BcvRate);

        return Ok(new
        {
            ok = true,
            message,
            totals = new
            {
                subtotal    = eval.Subtotal,
                deliveryFee = eval.DeliveryFee,
                totalUsd    = eval.TotalUsd,
                bsRate      = eval.BcvRate?.Rate,
                totalBs     = eval.TotalBs,
                isStaleRate = eval.BcvRate?.IsStale ?? false,
                rateLabel   = eval.BcvRate?.CurrencyLabel
            },
            hasPagoMobileConfig = eval.HasPagoMobileConfig
        });
    }

    /// <summary>
    /// GET /api/admin/handoffs/{conversationId}/draft/totals
    /// Lightweight totals + validation view of the current OperatorDraft.
    /// Returns 200 in every case (even when the draft is missing/invalid) so
    /// the dashboard can poll cheaply without producing 422 noise in the UI;
    /// the response carries `isComplete` + `missing[]` + `unpriced[]` for the
    /// frontend to decide what to render. Re-uses EvaluateDraftAsync so the
    /// numbers are identical to those PreviewDraftMessage computes.
    /// </summary>
    [HttpGet("{conversationId}/draft/totals")]
    public async Task<IActionResult> GetDraftTotals(string conversationId, CancellationToken ct)
    {
        if (!IsAuthorized()) return Unauthorized();

        var entity = await _db.ConversationStates.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ConversationId == conversationId, ct);
        if (entity is null) return NotFound(new { error = "Conversation not found" });
        if (!IsAuthorizedForConversation(entity))
            return Unauthorized(new { error = "Not authorized for this conversation's business" });

        var eval = await EvaluateDraftAsync(entity, ct);
        if (eval is null)
        {
            return Ok(new
            {
                isComplete           = false,
                missing              = new[] { "draft" },
                unpriced             = Array.Empty<string>(),
                subtotal             = 0m,
                deliveryFee          = 0m,
                totalUsd             = 0m,
                paymentMethod        = (string?)null,
                bsRate               = (decimal?)null,
                totalBs              = (decimal?)null,
                isStaleRate          = false,
                rateLabel            = (string?)null,
                hasPagoMobileConfig  = false
            });
        }

        return Ok(new
        {
            isComplete          = eval.IsComplete,
            missing             = eval.Missing,
            unpriced            = eval.Unpriced,
            subtotal            = eval.Subtotal,
            deliveryFee         = eval.DeliveryFee,
            totalUsd            = eval.TotalUsd,
            paymentMethod       = eval.Draft.PaymentMethod,
            bsRate              = eval.BcvRate?.Rate,
            totalBs             = eval.TotalBs,
            isStaleRate         = eval.BcvRate?.IsStale ?? false,
            rateLabel           = eval.BcvRate?.CurrencyLabel,
            hasPagoMobileConfig = eval.HasPagoMobileConfig
        });
    }

    /// <summary>
    /// POST /api/admin/handoffs/{conversationId}/create-order
    /// Promotes a structurally-valid OperatorDraft into a real Order via the
    /// canonical IOrderRepository.AddOrderAsync path — the SAME persistence the
    /// bot's checkout uses. Idempotent: a second call after success returns 409
    /// with the existing orderId. Side effects in order:
    ///   1. Validate draft via EvaluateDraftAsync (same as PreviewDraftMessage).
    ///   2. Build Domain.Entities.Order from the draft + conversation context.
    ///   3. Persist via IOrderRepository.AddOrderAsync (structural guards from
    ///      commit 3394329 apply — IX_Orders_ActivePending reuse, snapshot
    ///      protection, customer upsert + analytics).
    ///   4. Mutate conversation state: OrderCreatedByHumanId + LastOrderId set;
    ///      AwaitingPostConfirmProof set for pago_movil/divisas/zelle without
    ///      a proof yet; OperatorDraft cleared; HumanChatLog gets the receipt.
    ///   5. Send the same Msg.BuildReceipt copy the bot sends — customers
    ///      cannot tell the difference between a bot-driven and human-driven
    ///      order from the WhatsApp side.
    ///   6. NotifyOrderConfirmedAsync to the business's staff channel.
    /// </summary>
    [HttpPost("{conversationId}/create-order")]
    public async Task<IActionResult> CreateOrderFromDraft(string conversationId, CancellationToken ct)
    {
        if (!IsAuthorized()) return Unauthorized();

        var entity = await _db.ConversationStates.FindAsync(new object[] { conversationId }, ct);
        if (entity is null) return NotFound(new { error = "Conversation not found" });
        if (!IsAuthorizedForConversation(entity))
            return Unauthorized(new { error = "Not authorized for this conversation's business" });

        // Deserialize the typed conversation fields. We mutate this object,
        // re-serialize into entity.StateJson, and SaveChangesAsync — the same
        // round-trip pattern ConversationStateStore.SaveAsync uses inside the
        // bot pipeline (so the format stays consistent across writers).
        ConversationFields fields;
        try
        {
            fields = JsonSerializer.Deserialize<ConversationFields>(entity.StateJson ?? "{}", JsonOpts)
                     ?? new ConversationFields();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateOrderFromDraft: malformed StateJson for {ConversationId}", conversationId);
            return StatusCode(500, new { error = "Estado de conversación corrupto." });
        }

        // ── Idempotency ─────────────────────────────────────────────────────
        // A second click after success returns the existing orderId rather
        // than producing a duplicate row. Frontend disables the button on
        // first success, but a network retry or double-click race lands here.
        if (fields.OrderCreatedByHumanId is { } existingId)
        {
            return Conflict(new
            {
                error = "Ya se procesó un pedido para esta conversación.",
                orderId = existingId,
                orderNumber = FormatOrderNumber(existingId)
            });
        }

        // ── Validate ────────────────────────────────────────────────────────
        var eval = await EvaluateDraftAsync(entity, ct);
        if (eval is null)
        {
            return UnprocessableEntity(new
            {
                ok = false,
                error = "No hay borrador para esta conversación.",
                missing = new[] { "draft" },
                unpriced = Array.Empty<string>()
            });
        }
        if (!eval.IsComplete)
        {
            return UnprocessableEntity(new
            {
                ok = false,
                error = "El borrador no está completo para procesar el pedido.",
                missing = eval.Missing,
                unpriced = eval.Unpriced
            });
        }

        // ── Build Order ─────────────────────────────────────────────────────
        var parts = conversationId.Split(':');
        if (parts.Length < 2)
            return BadRequest(new { error = "Invalid conversationId format" });
        var customerFrom = parts[0];
        var phoneNumberId = parts[1];

        var draft = eval.Draft;
        var biz = eval.Business;
        var nowUtc = DateTime.UtcNow;

        // Prefer the operator-curated ProofMediaId on the draft; fall back to
        // the state-level PaymentProofMediaId captured by the bot's media path.
        var proofMediaId = !string.IsNullOrWhiteSpace(draft.ProofMediaId)
            ? draft.ProofMediaId
            : fields.PaymentProofMediaId;

        var order = new Order
        {
            BusinessId          = entity.BusinessId,
            From                = customerFrom,
            PhoneNumberId       = phoneNumberId,
            DeliveryType        = draft.DeliveryType!,
            CreatedAtUtc        = nowUtc,

            CustomerName        = draft.CustomerName,
            CustomerIdNumber    = draft.CustomerIdNumber,
            CustomerPhone       = draft.CustomerPhone,
            Address             = draft.Address,
            PaymentMethod       = draft.PaymentMethod,
            LocationText        = draft.LocationText,
            SpecialInstructions = draft.SpecialInstructions,

            CheckoutCompleted      = true,
            CheckoutCompletedAtUtc = nowUtc,
            CheckoutFormSent       = true,

            PaymentProofMediaId        = proofMediaId,
            PaymentProofSubmittedAtUtc = !string.IsNullOrWhiteSpace(proofMediaId) ? nowUtc : (DateTime?)null,

            Items = (draft.Items ?? new()).Select(i => new OrderItem
            {
                Name      = string.IsNullOrWhiteSpace(i.Modifiers) ? i.Name : $"{i.Name} ({i.Modifiers})",
                Quantity  = i.Quantity,
                UnitPrice = i.UnitPrice ?? 0m,
                LineTotal = (i.UnitPrice ?? 0m) * i.Quantity
            }).ToList(),

            // Authoritative delivery fee from the server (no client-supplied value).
            DeliveryFee = eval.DeliveryFee
        };
        // RecalculateTotal sums items × prices and adds DeliveryFee — same
        // as the bot's flow. Idempotent if called again later.
        order.RecalculateTotal();

        // ── Persist ─────────────────────────────────────────────────────────
        Order persisted;
        try
        {
            persisted = await _orderRepository.AddOrderAsync(order, ct);
        }
        catch (InvalidOperationException invOpEx)
        {
            // AddOrderAsync's TotalAmount > 0 guard or ReuseExistingPendingOrderAsync's
            // structural guard rejected the order. Surface as 422 so the panel can
            // tell the operator what to fix.
            _logger.LogWarning(invOpEx,
                "CreateOrderFromDraft: structural rejection for {ConversationId}", conversationId);
            return UnprocessableEntity(new
            {
                ok = false,
                error = invOpEx.Message,
                missing = new[] { "total" },
                unpriced = Array.Empty<string>()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "CreateOrderFromDraft: order persistence failed for {ConversationId}", conversationId);
            return StatusCode(500, new { error = "No se pudo persistir el pedido. Intenta de nuevo." });
        }

        var orderNumber = FormatOrderNumber(persisted.Id);

        // ── Mutate state ────────────────────────────────────────────────────
        fields.OrderCreatedByHumanId = persisted.Id;
        fields.LastOrderId           = persisted.Id;

        // Post-confirm proof capture: when the payment method requires evidence
        // and no proof was attached at creation time, leave the conversation in
        // a state where the next inbound image attaches to this order — same
        // semantics the bot uses at WebhookProcessor.cs:2761.
        if (persisted.PaymentMethod is "pago_movil" or "divisas" or "zelle"
            && string.IsNullOrWhiteSpace(persisted.PaymentProofMediaId))
        {
            fields.AwaitingPostConfirmProof = true;
        }

        // The draft has been consumed.
        fields.OperatorDraft = null;

        // Visible audit trail in the operator's chatLog.
        fields.HumanChatLog ??= new List<HumanChatEntry>();
        fields.HumanChatLog.Add(new HumanChatEntry
        {
            Sender = "bot",
            Text   = $"✅ Pedido #{orderNumber} creado.",
            Kind   = "text",
            At     = nowUtc
        });
        if (fields.HumanChatLog.Count > 50)
            fields.HumanChatLog = fields.HumanChatLog.Skip(fields.HumanChatLog.Count - 50).ToList();

        // Persist the mutated state. AddOrderAsync already SaveChangesAsync'd
        // the Order; this is a separate write on the ConversationState row.
        entity.StateJson = JsonSerializer.Serialize(fields, JsonOpts);
        entity.UpdatedAtUtc = nowUtc;
        await _db.SaveChangesAsync(ct);

        // ── Customer receipt (same template the bot's checkout sends) ───────
        var receipt = HandoffMessageBuilder.BuildReceipt(
            orderNumber:          orderNumber,
            customerName:         draft.CustomerName ?? "",
            customerIdNumber:     draft.CustomerIdNumber ?? "",
            customerPhone:        draft.CustomerPhone ?? customerFrom,
            items:                (draft.Items ?? new()).Select(i => new ConversationItemEntry
                                  {
                                      Name = i.Name ?? "",
                                      Quantity = i.Quantity,
                                      Modifiers = i.Modifiers,
                                      UnitPrice = i.UnitPrice ?? 0m
                                  }).ToList(),
            specialInstructions:  draft.SpecialInstructions,
            address:              draft.Address ?? "",
            paymentText:          HandoffMessageBuilder.PaymentMethodText(draft.PaymentMethod),
            deliveryType:         draft.DeliveryType ?? "",
            bcvRate:              eval.BcvRate,
            paymentMethod:        draft.PaymentMethod,
            paymentProofReceived: !string.IsNullOrWhiteSpace(proofMediaId));

        // Per-business WhatsApp access token resolution — same precedence the
        // existing Reply / VerifyPayment endpoints use. Fall back to env so
        // older single-tenant configs continue to work.
        string? accessToken = biz?.AccessToken
            ?? Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN")
            ?? Environment.GetEnvironmentVariable("META_ACCESS_TOKEN");

        bool customerNotified = false;
        try
        {
            customerNotified = await _whatsAppClient.SendTextMessageAsync(new OutgoingMessage
            {
                To            = customerFrom,
                Body          = receipt,
                PhoneNumberId = phoneNumberId,
                AccessToken   = accessToken
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "CreateOrderFromDraft: customer receipt send failed for order {OrderId}", persisted.Id);
        }

        // Append the actual receipt body to the operator transcript so the
        // operator can see what the customer just received. Best-effort —
        // if the chatLog write fails it doesn't roll back the order.
        try
        {
            fields.HumanChatLog.Add(new HumanChatEntry
            {
                Sender = "bot",
                Text   = receipt,
                Kind   = "text",
                At     = DateTime.UtcNow
            });
            if (fields.HumanChatLog.Count > 50)
                fields.HumanChatLog = fields.HumanChatLog.Skip(fields.HumanChatLog.Count - 50).ToList();
            entity.StateJson = JsonSerializer.Serialize(fields, JsonOpts);
            entity.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "CreateOrderFromDraft: chatLog tail write failed for order {OrderId}", persisted.Id);
        }

        // ── Staff notification ──────────────────────────────────────────────
        if (biz is not null)
        {
            try
            {
                var businessContext = new BusinessContext(
                    BusinessId:        biz.Id,
                    PhoneNumberId:     biz.PhoneNumberId ?? phoneNumberId,
                    AccessToken:       accessToken ?? "",
                    BusinessName:      biz.Name ?? "",
                    NotificationPhone: biz.NotificationPhone);

                var itemsSummary = string.Join(", ",
                    (draft.Items ?? new()).Select(i => $"{i.Quantity} {i.Name}"));
                var totalText = $"${eval.TotalUsd:0.00}";

                await _notificationService.NotifyOrderConfirmedAsync(
                    businessContext, draft.CustomerName ?? "?", itemsSummary, totalText, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "CreateOrderFromDraft: staff notification failed for order {OrderId}", persisted.Id);
            }
        }

        _logger.LogInformation(
            "HANDOFF ORDER CREATED: conversationId={ConversationId} orderId={OrderId} orderNumber={OrderNumber} " +
            "items={Items} subtotal={Sub} fee={Fee} total={Total} payment={Pay} customerNotified={Notified}",
            conversationId, persisted.Id, orderNumber,
            persisted.Items.Count, persisted.SubtotalAmount, persisted.DeliveryFee, persisted.TotalAmount,
            persisted.PaymentMethod, customerNotified);

        return Ok(new
        {
            ok = true,
            orderId = persisted.Id,
            orderNumber,
            customerNotified
        });
    }

    // Shared formatter for the customer-facing 8-char order number — same
    // scheme the bot's checkout uses at WebhookProcessor.cs:2764.
    private static string FormatOrderNumber(Guid orderId)
        => orderId.ToString("N")[..8].ToUpperInvariant();

    // ── Draft helpers (private) ──

    /// <summary>
    /// Single source of truth for draft validation + total computation +
    /// business / BCV resolution. Returns null when no draft exists on the
    /// conversation; otherwise an immutable evaluation record that both the
    /// preview-message endpoint and the totals endpoint render.
    ///
    /// Validation mirrors the structural-guard contract from commit 3394329:
    /// items > 0, every unitPrice > 0, customer slots present, deliveryType
    /// + address-when-delivery, paymentMethod, and total > deliveryFee.
    /// </summary>
    private async Task<DraftEvaluation?> EvaluateDraftAsync(ConversationState entity, CancellationToken ct)
    {
        var draft = ReadDraftFromStateJson(entity.StateJson);
        if (draft is null) return null;

        var missing = new List<string>();
        var unpriced = new List<string>();

        if (string.IsNullOrWhiteSpace(draft.CustomerName))     missing.Add("customerName");
        if (string.IsNullOrWhiteSpace(draft.CustomerIdNumber)) missing.Add("customerIdNumber");
        if (string.IsNullOrWhiteSpace(draft.CustomerPhone))    missing.Add("customerPhone");
        if (string.IsNullOrWhiteSpace(draft.DeliveryType))     missing.Add("deliveryType");
        if (string.IsNullOrWhiteSpace(draft.PaymentMethod))    missing.Add("paymentMethod");
        if (draft.DeliveryType == "delivery" && string.IsNullOrWhiteSpace(draft.Address))
            missing.Add("address");
        if (draft.Items is null || draft.Items.Count == 0)
            missing.Add("items");

        if (draft.Items is { Count: > 0 })
        {
            foreach (var i in draft.Items)
            {
                if (!(i.UnitPrice is { } up && up > 0m))
                    unpriced.Add(string.IsNullOrWhiteSpace(i.Name) ? "(sin nombre)" : i.Name);
            }
        }

        var subtotal = 0m;
        if (draft.Items is { Count: > 0 })
            foreach (var i in draft.Items)
                if (i.UnitPrice is { } up && up > 0m)
                    subtotal += up * i.Quantity;
        var deliveryFee = draft.DeliveryType == "delivery" ? HandoffMessageBuilder.DeliveryFeeUsd : 0m;
        var totalUsd = subtotal + deliveryFee;

        if (missing.Count == 0 && unpriced.Count == 0 && totalUsd <= deliveryFee)
            missing.Add("total");

        Business? biz = null;
        if (entity.BusinessId.HasValue)
        {
            biz = await _db.Businesses.AsNoTracking()
                .Where(b => b.Id == entity.BusinessId.Value)
                .FirstOrDefaultAsync(ct);
        }

        ResolvedRate? bcvRate = null;
        try
        {
            bcvRate = await _exchangeRateProvider.GetRateAsync(biz?.CurrencyReference, ct);
        }
        catch (Exception ex)
        {
            // Non-fatal — totals + message still render without the Bs line.
            _logger.LogWarning(ex,
                "EvaluateDraftAsync: BCV rate fetch failed for conversation {ConversationId}", entity.ConversationId);
        }

        decimal? totalBs = null;
        if (bcvRate is { Rate: > 0 } && totalUsd > 0)
            totalBs = totalUsd * bcvRate.Rate;

        var hasPagoMobileConfig =
            !string.IsNullOrWhiteSpace(biz?.PaymentMobileBank)
            && !string.IsNullOrWhiteSpace(biz?.PaymentMobileId)
            && !string.IsNullOrWhiteSpace(biz?.PaymentMobilePhone);

        var isComplete = missing.Count == 0 && unpriced.Count == 0;

        return new DraftEvaluation(
            draft, biz, bcvRate,
            isComplete, missing, unpriced,
            subtotal, deliveryFee, totalUsd, totalBs, hasPagoMobileConfig);
    }

    private sealed record DraftEvaluation(
        OperatorDraft Draft,
        Business? Business,
        ResolvedRate? BcvRate,
        bool IsComplete,
        List<string> Missing,
        List<string> Unpriced,
        decimal Subtotal,
        decimal DeliveryFee,
        decimal TotalUsd,
        decimal? TotalBs,
        bool HasPagoMobileConfig);

    // ── Draft state-JSON helpers (private) ──

    private static OperatorDraft? ReadDraftFromStateJson(string? stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(stateJson);
            if (doc.RootElement.TryGetProperty("operatorDraft", out var prop)
                && prop.ValueKind == JsonValueKind.Object)
                return prop.Deserialize<OperatorDraft>(JsonOpts);
        }
        catch { /* malformed JSON ⇒ treat as no draft */ }
        return null;
    }

    private (string? error, OperatorDraft? current) CheckDraftConcurrency(string? stateJson)
    {
        var current = ReadDraftFromStateJson(stateJson);
        if (!Request.Headers.TryGetValue("If-Match", out var hdr) || string.IsNullOrWhiteSpace(hdr))
            return (null, current);

        if (!DateTime.TryParse(hdr.ToString().Trim(), out var expected))
            return ("If-Match header is not a valid ISO-8601 timestamp", current);

        var currentTs = current?.UpdatedAtUtc;
        if (currentTs is null)
            return ("Draft does not exist on server (stale If-Match)", current);

        if (Math.Abs((currentTs.Value - expected).TotalSeconds) > 1)
            return ("Draft was updated by another session", current);

        return (null, current);
    }

    private async Task WriteDraftAsync(ConversationState entity, OperatorDraft draft, CancellationToken ct)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(entity.StateJson ?? "{}", JsonOpts) ?? new();
        dict["operatorDraft"] = draft;
        entity.StateJson = JsonSerializer.Serialize(dict, JsonOpts);
        entity.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private static OperatorDraft MergeDraftPatch(OperatorDraft baseDraft, OperatorDraftPatchRequest patch)
    {
        return new OperatorDraft
        {
            // Items is special-cased: null in the patch means "leave alone";
            // a non-null list (including []) replaces the current list outright.
            Items               = patch.Items                ?? baseDraft.Items,
            CustomerName        = patch.CustomerName         ?? baseDraft.CustomerName,
            CustomerIdNumber    = patch.CustomerIdNumber     ?? baseDraft.CustomerIdNumber,
            CustomerPhone       = patch.CustomerPhone        ?? baseDraft.CustomerPhone,
            Address             = patch.Address              ?? baseDraft.Address,
            DeliveryType        = patch.DeliveryType         ?? baseDraft.DeliveryType,
            PaymentMethod       = patch.PaymentMethod        ?? baseDraft.PaymentMethod,
            SpecialInstructions = patch.SpecialInstructions  ?? baseDraft.SpecialInstructions,
            LocationText        = patch.LocationText         ?? baseDraft.LocationText,
            ProofMediaId        = patch.ProofMediaId         ?? baseDraft.ProofMediaId,
            // UpdatedAtUtc is always server-set on write — never accepted from the client.
            UpdatedAtUtc        = baseDraft.UpdatedAtUtc
        };
    }

    /// <summary>
    /// PATCH body — every field is optional. Items is the only collection field;
    /// null means "leave the existing list alone", a non-null list replaces it.
    /// </summary>
    public sealed class OperatorDraftPatchRequest
    {
        public List<OperatorDraftItem>? Items { get; set; }
        public string? CustomerName { get; set; }
        public string? CustomerIdNumber { get; set; }
        public string? CustomerPhone { get; set; }
        public string? Address { get; set; }
        public string? DeliveryType { get; set; }
        public string? PaymentMethod { get; set; }
        public string? SpecialInstructions { get; set; }
        public string? LocationText { get; set; }
        public string? ProofMediaId { get; set; }
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
