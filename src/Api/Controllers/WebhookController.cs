using System.Text;
using System.Text.Json;
using Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Serilog;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.DTOs;
using WhatsAppSaaS.Application.Interfaces;

namespace Api.Controllers;

[ApiController]
[Route("webhook")]
[Route("api/webhook")]
public class WebhookController : ControllerBase
{
    private readonly IBusinessResolver _businessResolver;
    private readonly IWebhookProcessor _processor;
    private readonly ISignatureValidator _signatureValidator;
    private readonly WhatsAppOptions _whatsAppOptions;

    private const int MaxBodySize = 256 * 1024; // 256 KB

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public WebhookController(
        IBusinessResolver businessResolver,
        IWebhookProcessor processor,
        ISignatureValidator signatureValidator,
        IOptions<WhatsAppOptions> whatsAppOptions)
    {
        _businessResolver = businessResolver;
        _processor = processor;
        _signatureValidator = signatureValidator;
        _whatsAppOptions = whatsAppOptions.Value;
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

        if (string.IsNullOrWhiteSpace(expectedToken) || expectedToken == "your-verify-token-here")
        {
            Log.Error("Webhook verification failed: WHATSAPP_VERIFY_TOKEN not configured.");
            return StatusCode(500, "Verify token not configured");
        }

        if (verifyToken != expectedToken)
        {
            Log.Warning("Webhook verification failed: token mismatch");
            return StatusCode(403);
        }

        Log.Information("Webhook verified successfully");
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

        if (_whatsAppOptions.RequireSignatureValidation)
        {
            var sig = Request.Headers["X-Hub-Signature-256"].FirstOrDefault();
            if (!_signatureValidator.IsValid(rawBody, sig ?? ""))
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

        BusinessContext? businessContext;
        try
        {
            businessContext = await _businessResolver.ResolveByPhoneNumberIdAsync(phoneNumberId, ct);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "WEBHOOK ERROR: business lookup failed for phone_number_id={PhoneNumberId}", phoneNumberId);
            return Ok(); // return 200 so Meta does not retry endlessly
        }

        if (businessContext is null)
        {
            Log.Warning("WEBHOOK: no business for phone_number_id={PhoneNumberId}", phoneNumberId);
            return Ok();
        }

        Log.Information("WEBHOOK PROCESSING: businessId={BusinessId} phoneNumberId={PhoneNumberId} entries={EntryCount}",
            businessContext.BusinessId, phoneNumberId, payload.Entry?.Count ?? 0);

        await _processor.ProcessAsync(payload, businessContext, ct);
        return Ok();
    }
}
