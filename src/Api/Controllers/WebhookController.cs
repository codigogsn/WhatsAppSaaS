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
    private readonly bool _isProduction;

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
        _isProduction = hostEnvironment.IsProduction();
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

        // Signature validation: ALWAYS required in production, auto-enabled in dev when AppSecret is set
        var requireSig = _isProduction
                         || _whatsAppOptions.RequireSignatureValidation
                         || !string.IsNullOrEmpty(_whatsAppOptions.AppSecret);
        if (requireSig)
        {
            var sig = Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
            if (!_signatureValidator.IsValid(rawBody, sig ?? ""))
            {
                Log.Warning("WEBHOOK REJECTED: invalid signature on {Path} (production={IsProduction})", Request.Path, _isProduction);
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

        var firstMessageId = payload.Entry?
            .FirstOrDefault()?
            .Changes?
            .FirstOrDefault()?
            .Value?
            .Messages?
            .FirstOrDefault()?
            .Id;

        var phoneNumberId = payload.Entry?
            .FirstOrDefault()?
            .Changes?
            .FirstOrDefault()?
            .Value?
            .Metadata?
            .PhoneNumberId;

        Log.Information("WEBHOOK HIT: POST {Path} phoneNumberId={PhoneNumberId} messageId={MessageId}",
            Request.Path, phoneNumberId ?? "(none)", firstMessageId ?? "(none)");

        if (string.IsNullOrWhiteSpace(phoneNumberId))
            return Ok();

        // Skip non-message webhooks (status callbacks, delivery/read receipts, etc.)
        // These have phoneNumberId but no actual messages to process.
        if (string.IsNullOrWhiteSpace(firstMessageId))
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
            Log.Error(ex, "WEBHOOK ERROR: business lookup failed for phone_number_id={PhoneNumberId}", phoneNumberId);
            return Ok(); // return 200 so Meta does not retry endlessly
        }

        if (businessContext is null)
        {
            Log.Warning("WEBHOOK: could not resolve or create business for phone_number_id={PhoneNumberId}", phoneNumberId);
            return Ok();
        }

        Log.Information("INCOMING MSG: {From} | {BusinessId}",
            phoneNumberId, businessContext.BusinessId);

        Log.Information("WEBHOOK ENQUEUING: businessId={BusinessId} phoneNumberId={PhoneNumberId} messageId={MessageId}",
            businessContext.BusinessId, phoneNumberId, firstMessageId ?? "(none)");

        try
        {
            await _messageQueue.EnqueueAsync(new QueuedMessage(payload, businessContext), firstMessageId, ct);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WEBHOOK ENQUEUE FAILED: queue insert failed for businessId={BusinessId} messageId={MessageId} — returning 500 so Meta retries",
                businessContext.BusinessId, firstMessageId ?? "(none)");
            WhatsAppSaaS.Api.Diagnostics.AppMetrics.RecordEnqueueFailure();
            return StatusCode(500);
        }

        return Ok();
    }
}
