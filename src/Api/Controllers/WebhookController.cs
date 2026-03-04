using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
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

    [HttpGet]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (string.Equals(mode, "subscribe", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(verifyToken, _options.VerifyToken, StringComparison.Ordinal))
        {
            return Ok(challenge);
        }

        return StatusCode(StatusCodes.Status403Forbidden, "Verification failed");
    }

    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        HttpContext.Request.EnableBuffering();

        using var reader = new StreamReader(HttpContext.Request.Body, leaveOpen: true);
        var rawBody = await reader.ReadToEndAsync(cancellationToken);
        HttpContext.Request.Body.Position = 0;

        if (_options.RequireSignatureValidation)
        {
            var signature = HttpContext.Request.Headers["X-Hub-Signature-256"].FirstOrDefault() ?? "";

            if (!_signatureValidator.IsValid(rawBody, signature))
            {
                _logger.LogWarning("Invalid webhook signature");
                return Unauthorized();
            }
        }

        WebhookPayload? payload;

        try
        {
            payload = JsonSerializer.Deserialize<WebhookPayload>(rawBody,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return BadRequest("Invalid JSON payload");
        }

        if (payload == null)
            return BadRequest("Empty payload");

        var validation = await _payloadValidator.ValidateAsync(payload, cancellationToken);

        if (!validation.IsValid)
            return BadRequest(validation.Errors.Select(e => e.ErrorMessage));

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
                var logger = HttpContext.RequestServices.GetRequiredService<ILogger<WebhookController>>();
                logger.LogError(ex, "Webhook processing error");
            }
        });

        return Ok();
    }
}
