using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.DTOs;
using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Api.Controllers;

[ApiController]
[Route("webhook")]
public sealed class WebhookController : ControllerBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISignatureValidator _signatureValidator;
    private readonly WhatsAppOptions _options;
    private readonly IValidator<WebhookPayload> _payloadValidator;
    private readonly ILogger<WebhookController> _logger;

    public WebhookController(
        IServiceScopeFactory scopeFactory,
        ISignatureValidator signatureValidator,
        IOptions<WhatsAppOptions> options,
        IValidator<WebhookPayload> payloadValidator,
        ILogger<WebhookController> logger)
    {
        _scopeFactory = scopeFactory;
        _signatureValidator = signatureValidator;
        _options = options.Value;
        _payloadValidator = payloadValidator;
        _logger = logger;
    }

    /// <summary>
    /// Webhook verification endpoint for Meta's subscription handshake.
    /// </summary>
    [HttpGet]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        _logger.LogInformation(
            "Webhook verification request: mode={Mode}, token_present={TokenPresent}",
            mode, !string.IsNullOrEmpty(verifyToken));

        if (string.Equals(mode, "subscribe", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(verifyToken, _options.VerifyToken, StringComparison.Ordinal))
        {
            _logger.LogInformation("Webhook verification succeeded");
            return Ok(challenge);
        }

        _logger.LogWarning("Webhook verification failed: mode={Mode}, token_match={TokenMatch}",
            mode, string.Equals(verifyToken, _options.VerifyToken));

        return StatusCode(StatusCodes.Status403Forbidden, "Verification failed");
    }

    /// <summary>
    /// Receives incoming webhook events from WhatsApp Cloud API.
    /// Returns 200 immediately, then processes the message asynchronously.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        // Read raw body for signature validation
        HttpContext.Request.EnableBuffering();
        using var reader = new StreamReader(HttpContext.Request.Body, leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync(cancellationToken);
        HttpContext.Request.Body.Position = 0;

        // Optional signature validation
        if (_options.RequireSignatureValidation)
        {
            var signature = HttpContext.Request.Headers["X-Hub-Signature-256"].FirstOrDefault() ?? string.Empty;
            if (!_signatureValidator.IsValid(rawBody, signature))
            {
                _logger.LogWarning("Invalid webhook signature from {RemoteIp}",
                    HttpContext.Connection.RemoteIpAddress);
                return Unauthorized("Invalid signature");
            }
        }

        // Deserialize payload
        WebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<WebhookPayload>(rawBody, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize webhook payload");
            return BadRequest("Invalid JSON payload");
        }

        if (payload is null)
        {
            return BadRequest("Empty payload");
        }

        // Validate payload structure
        var validationResult = await _payloadValidator.ValidateAsync(payload, cancellationToken);
        if (!validationResult.IsValid)
        {
            _logger.LogWarning("Webhook payload validation failed: {Errors}",
                string.Join(", ", validationResult.Errors.Select(e => e.ErrorMessage)));
            return BadRequest(validationResult.Errors.Select(e => e.ErrorMessage));
        }

        // Fire and forget processing - return 200 immediately per Meta's requirements
        // IMPORTANT: Create a new DI scope so scoped services (DbContext) are NOT disposed.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<IWebhookProcessor>();

                await processor.ProcessAsync(payload, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error processing webhook payload");
            }
        }, CancellationToken.None);

        return Ok();
    }
}
