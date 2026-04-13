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

        // Build tenant-scoped payloads: one filtered payload per phoneNumberId.
        // Meta can batch messages from different phone numbers in one delivery.
        // Each enqueued job must contain ONLY messages for its own tenant —
        // never the full original payload, which could leak cross-tenant messages.
        var groupedPayloads = new Dictionary<string, (WebhookPayload Payload, List<string> MessageIds)>();

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

                        if (!groupedPayloads.TryGetValue(pnId, out var group))
                        {
                            // Create a new filtered payload for this phoneNumberId
                            group = (new WebhookPayload { Object = payload.Object, Entry = [] }, new List<string>());
                            groupedPayloads[pnId] = group;
                        }

                        group.MessageIds.Add(msg.Id);

                        // Find or create a matching entry → change → value in the filtered payload
                        var filteredEntry = group.Payload.Entry.FirstOrDefault(e => e.Id == entry.Id);
                        if (filteredEntry is null)
                        {
                            filteredEntry = new WebhookEntry { Id = entry.Id, Changes = [] };
                            group.Payload.Entry.Add(filteredEntry);
                        }

                        // Each change is scoped to one phoneNumberId, so find-or-create by metadata match
                        var filteredChange = filteredEntry.Changes.FirstOrDefault(c =>
                            c.Value?.Metadata?.PhoneNumberId == pnId && c.Field == change!.Field);
                        if (filteredChange is null)
                        {
                            filteredChange = new WebhookChange
                            {
                                Field = change!.Field,
                                Value = new WebhookChangeValue
                                {
                                    MessagingProduct = value.MessagingProduct,
                                    Metadata = value.Metadata,
                                    Contacts = value.Contacts,
                                    Statuses = value.Statuses,
                                    Messages = []
                                }
                            };
                            filteredEntry.Changes.Add(filteredChange);
                        }

                        filteredChange.Value!.Messages!.Add(msg);
                    }
                }
            }
        }

        var totalMessages = groupedPayloads.Values.Sum(g => g.MessageIds.Count);
        Log.Information("WEBHOOK HIT: POST {Path} phoneNumbers={PhoneCount} messageCount={MessageCount}",
            Request.Path, groupedPayloads.Count, totalMessages);

        if (groupedPayloads.Count == 0)
        {
            Log.Debug("WEBHOOK SKIPPED: no inbound messages (status callback / receipt)");
            return Ok();
        }

        // Enqueue each tenant-scoped payload independently.
        var enqueuedCount = 0;
        foreach (var (phoneNumberId, (filteredPayload, messageIds)) in groupedPayloads)
        {
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

            try
            {
                foreach (var msgId in messageIds)
                {
                    Log.Information("WEBHOOK ENQUEUING: businessId={BusinessId} phoneNumberId={PhoneNumberId} messageId={MessageId}",
                        businessContext.BusinessId, phoneNumberId, msgId);
                    await _messageQueue.EnqueueAsync(new QueuedMessage(filteredPayload, businessContext), msgId, ct);
                    enqueuedCount++;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "WEBHOOK ENQUEUE FAILED: businessId={BusinessId} enqueued={Enqueued}/{Total} — returning 500 so Meta retries",
                    businessContext.BusinessId, enqueuedCount, totalMessages);
                WhatsAppSaaS.Api.Diagnostics.AppMetrics.RecordEnqueueFailure();
                return StatusCode(500);
            }
        }

        return Ok();
    }
}
