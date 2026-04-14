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

        // Signature validation: required in ALL non-Development environments.
        // Fail-closed: production MUST have AppSecret configured; reject everything otherwise.
        var requireSig = _isNonDevelopment
                         || _whatsAppOptions.RequireSignatureValidation
                         || !string.IsNullOrEmpty(_whatsAppOptions.AppSecret);

        if (_isNonDevelopment && string.IsNullOrEmpty(_whatsAppOptions.AppSecret))
        {
            Log.Error("WEBHOOK REJECTED: AppSecret not configured in production — fail-closed. Path={Path}", Request.Path);
            return StatusCode(500);
        }

        if (requireSig)
        {
            var sig = Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
            if (string.IsNullOrEmpty(sig))
            {
                Log.Warning("WEBHOOK REJECTED: missing X-Hub-Signature-256 header on {Path}", Request.Path);
                return Unauthorized();
            }
            if (!_signatureValidator.IsValid(rawBody, sig))
            {
                Log.Warning("WEBHOOK REJECTED: invalid signature on {Path}", Request.Path);
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

        // Extract individual messages and build one queue item per inbound message.
        // Each queue item payload contains ONLY the single message it is responsible for,
        // so retries, failures, and fallback replies are scoped to the correct message/customer.
        var messageItems = new List<(string PhoneNumberId, string MessageId, WebhookPayload SingleMessagePayload)>();

        if (payload.Entry is not null)
        {
            foreach (var entry in payload.Entry)
            {
                if (entry?.Changes is null) continue;
                foreach (var change in entry.Changes)
                {
                    var value = change?.Value;
                    if (value?.Messages is null) continue;

                    var pnId = value.Metadata?.PhoneNumberId;
                    if (string.IsNullOrWhiteSpace(pnId)) continue;

                    foreach (var msg in value.Messages)
                    {
                        if (msg is null || string.IsNullOrWhiteSpace(msg.Id)) continue;

                        // Build a single-message payload for this specific inbound message
                        var singlePayload = new WebhookPayload
                        {
                            Object = payload.Object,
                            Entry =
                            [
                                new WebhookEntry
                                {
                                    Id = entry.Id,
                                    Changes =
                                    [
                                        new WebhookChange
                                        {
                                            Field = change!.Field,
                                            Value = new WebhookChangeValue
                                            {
                                                MessagingProduct = value.MessagingProduct,
                                                Metadata = value.Metadata,
                                                Contacts = value.Contacts,
                                                Messages = [msg]
                                            }
                                        }
                                    ]
                                }
                            ]
                        };

                        messageItems.Add((pnId, msg.Id, singlePayload));
                    }
                }
            }
        }

        Log.Information("WEBHOOK HIT: POST {Path} messageCount={MessageCount}",
            Request.Path, messageItems.Count);

        if (messageItems.Count == 0)
        {
            Log.Debug("WEBHOOK SKIPPED: no inbound messages (status callback / receipt)");
            return Ok();
        }

        // Enqueue each single-message payload independently.
        var enqueuedCount = 0;
        // Cache business resolution per phoneNumberId to avoid redundant lookups
        var businessCache = new Dictionary<string, BusinessContext>();

        foreach (var (phoneNumberId, msgId, singlePayload) in messageItems)
        {
            if (!businessCache.TryGetValue(phoneNumberId, out var businessContext))
            {
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

                businessCache[phoneNumberId] = businessContext;
            }

            try
            {
                Log.Information("WEBHOOK ENQUEUING: businessId={BusinessId} phoneNumberId={PhoneNumberId} messageId={MessageId}",
                    businessContext.BusinessId, phoneNumberId, msgId);
                await _messageQueue.EnqueueAsync(new QueuedMessage(singlePayload, businessContext), msgId, ct);
                enqueuedCount++;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "WEBHOOK ENQUEUE FAILED: businessId={BusinessId} enqueued={Enqueued}/{Total} — returning 500 so Meta retries",
                    businessContext.BusinessId, enqueuedCount, messageItems.Count);
                WhatsAppSaaS.Api.Diagnostics.AppMetrics.RecordEnqueueFailure();
                return StatusCode(500);
            }
        }

        return Ok();
    }
}
