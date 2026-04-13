using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Infrastructure.WhatsApp;

public sealed class SignatureValidator : ISignatureValidator
{
    private readonly WhatsAppOptions _options;
    private readonly ILogger<SignatureValidator> _logger;

    public SignatureValidator(
        IOptions<WhatsAppOptions> options,
        ILogger<SignatureValidator> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsValid(string payload, string signature)
    {
        if (string.IsNullOrEmpty(_options.AppSecret))
        {
            _logger.LogError("SIGNATURE VALIDATION FAIL-CLOSED: AppSecret not configured — rejecting request");
            return false;
        }

        if (string.IsNullOrEmpty(signature))
        {
            _logger.LogWarning("SIGNATURE VALIDATION: missing X-Hub-Signature-256 header");
            return false;
        }

        // Signature format: "sha256=<hex>"
        const string prefix = "sha256=";
        if (!signature.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Invalid signature format: missing sha256= prefix");
            return false;
        }

        var expectedHex = signature[prefix.Length..];

        var keyBytes = Encoding.UTF8.GetBytes(_options.AppSecret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(payloadBytes);
        var computedHex = Convert.ToHexString(hash).ToLowerInvariant();

        var isValid = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedHex),
            Encoding.UTF8.GetBytes(expectedHex.ToLowerInvariant()));

        if (!isValid)
        {
            _logger.LogWarning("Signature validation failed");
        }

        return isValid;
    }
}
