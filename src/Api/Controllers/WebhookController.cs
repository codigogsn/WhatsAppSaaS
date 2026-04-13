using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Serilog;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.DTOs;
using WhatsAppSaaS.Application.Interfaces;

namespace Api.Controllers;

[ApiController]
[Route("webhook")]
[Route("api/webhook")]
[EnableRateLimiting("webhook")]
public class WebhookController : ControllerBase
{
    private readonly IBusinessResolver _businessResolver;
    private readonly IMessageQueue _messageQueue;
    private readonly ISignatureValidator _signatureValidator;
    private readonly WhatsAppOptions _whatsAppOptions;
    private readonly bool _isNonDevelopment;

    private const int MaxBodySize = 256 * 1024; // 256 KB

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public WebhookController(
        IBusinessResolver businessResolver,
        IMessageQueue messageQueue,
        ISignatureValidator signatureValidator,
        IOptions<WhatsAppOptions> whatsAppOptions,
        IHostEnvironment hostEnvironment)
    {
        _businessResolver = businessResolver;
        _messageQueue = messageQueue;
        _signatureValidator = signatureValidator;
        _whatsAppOptions = whatsAppOptions.Value;
        _isNonDevelopment = !hostEnvironment.IsDevelopment();
    }

    // GET /webhook and /api/webhook -- Meta webhook verification
    [HttpGet]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        Log.Information("WEBHOOK HIT: GET {Path} at {Timestamp}",
            Request.Path, DateTime.UtcNow.ToString("O"));

        if (mode != "subscribe")
        {
            Log.Warning("Webhook verification failed: hub.mode={Mode}", mode);
            return StatusCode(403);
        }

        // Token resolution: env var > appsettings
        var expectedToken = Environment.GetEnvironmentVariable("WHATSAPP_VERIFY_TOKEN");
        if (string.IsNullOrWhiteSpace(expectedToken))
            expectedToken = _whatsAppOptions.VerifyToken;

        if (string.IsNullOrWhiteSpace(expectedToken)
            || expectedToken == "your-verify-token-here"
            || expectedToken == "dev-verify-token-123")
        {
            Log.Error("Webhook verification failed: WHATSAPP_VERIFY_TOKEN not configured (set env var).");
            return StatusCode(500, "Verify token not configured");
        }

        if (string.IsNullOrWhiteSpace(verifyToken)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(verifyToken),
                Encoding.UTF8.GetBytes(expectedToken)))
        {
            Log.Warning("WEBHOOK VERIFY FAILED: token mismatch");
            return StatusCode(403);
        }

        Log.Information("WEBHOOK VERIFY SUCCESS");
        return Ok(challenge);
    }

    // POST /webhook and /api/webhook
    [HttpPost]
    public async Task<IActionResult> Post(CancellationToken ct)
    {
        Log.Information("WEBHOOK HIT: POST {Path} at {Timestamp}",
            Request.Path, DateTime.UtcNow.ToString("O"));

        if (Request.ContentLength > MaxBodySize)
            return BadRequest("Payload too large");

        string rawBody;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
            rawBody = await reader.ReadToEndAsync(ct);

        if (rawBody.Length > MaxBodySize)
            return BadRequest("Payload too large");

        // Signature validation: required in ALL non-Development environments
        var requireSig = _isNonDevelopment
                         || _whatsAppOptions.RequireSignatureValidation
                         || !string.IsNullOrEmpty(_whatsAppOptions.AppSecret);
        if (requireSig)
        {
            var sig = Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
            if (!_signatureValidator.IsValid(rawBody, sig ?? ""))
            {
                Log.Warning("WEBHOOK REJECTED: invalid signature on {Path} (nonDev={IsNonDevelopment})", Request.Path, _isNonDevelopment);
                return Unauthorized();
            }
        }

        WebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<WebhookPayload>(rawBody, JsonOptions);
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "WEBHOOK REJECTED: invalid JSON on {Path}", Request.Path);
            return BadRequest();
        }

        if (payload is null)
        {
            Log.Warning("WEBHOOK REJECTED: null payload on {Path}", Request.Path);
            return BadRequest();
        }

        // Collect ALL message IDs from the payload for per-message dedup.
        // Meta can batch multiple messages in a single webhook delivery.
        // Deduplicating by only the first message ID would silently drop
        // new messages that share a batch with a previously-seen first message.
        var allMessageIds = new List<string>();
        string? phoneNumberId = null;

        if (payload.Entry is not null)
        {
            foreach (var entry in payload.Entry)
            {
                if (entry?.Changes is null) continue;
                foreach (var change in entry.Changes)
                {
                    var value = change?.Value;
                    if (value is null) continue;

                    phoneNumberId ??= value.Metadata?.PhoneNumberId;

                    if (value.Messages is not null)
                    {
                        foreach (var msg in value.Messages)
                        {
                            if (!string.IsNullOrWhiteSpace(msg?.Id))
                                allMessageIds.Add(msg.Id);
                        }
                    }
                }
            }
        }

        Log.Information("WEBHOOK HIT: POST {Path} phoneNumberId={PhoneNumberId} messageCount={MessageCount} messageIds={MessageIds}",
            Request.Path, phoneNumberId ?? "(none)", allMessageIds.Count,
            allMessageIds.Count > 0 ? string.Join(",", allMessageIds) : "(none)");

        if (string.IsNullOrWhiteSpace(phoneNumberId))
            return Ok();

        // Skip non-message webhooks (status callbacks, delivery/read receipts, etc.)
        // These have phoneNumberId but no actual messages to process.
        if (allMessageIds.Count == 0)
        {
            Log.Debug("WEBHOOK SKIPPED: no inbound messages (status callback / receipt), phoneNumberId={PhoneNumberId}", phoneNumberId);
            return Ok();
        }

        BusinessContext? businessContext;
        try
        {
            businessContext = await _businessResolver.ResolveOrCreateAsync(phoneNumberId, ct);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WEBHOOK ERROR: business lookup failed for phone_number_id={PhoneNumberId} — returning 500 so Meta retries", phoneNumberId);
            return StatusCode(500);
        }

        if (businessContext is null)
        {
            Log.Warning("WEBHOOK: could not resolve or create business for phone_number_id={PhoneNumberId} — returning 500 so Meta retries", phoneNumberId);
            return StatusCode(500);
        }

        Log.Information("INCOMING MSG: {From} | {BusinessId}",
            phoneNumberId, businessContext.BusinessId);

        // Enqueue one queue item per message ID for per-message dedup.
        // Each item carries the full payload; the processor's own per-message
        // MarkMessageProcessedAsync ensures only new messages are acted upon.
        var enqueuedCount = 0;
        try
        {
            foreach (var msgId in allMessageIds)
            {
                Log.Information("WEBHOOK ENQUEUING: businessId={BusinessId} phoneNumberId={PhoneNumberId} messageId={MessageId}",
                    businessContext.BusinessId, phoneNumberId, msgId);
                await _messageQueue.EnqueueAsync(new QueuedMessage(payload, businessContext), msgId, ct);
                enqueuedCount++;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WEBHOOK ENQUEUE FAILED: queue insert failed for businessId={BusinessId} enqueued={Enqueued}/{Total} — returning 500 so Meta retries",
                businessContext.BusinessId, enqueuedCount, allMessageIds.Count);
            WhatsAppSaaS.Api.Diagnostics.AppMetrics.RecordEnqueueFailure();
            return StatusCode(500);
        }

        return Ok();
    }
}
