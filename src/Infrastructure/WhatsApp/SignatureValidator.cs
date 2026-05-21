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
        // All configured secrets: primary AppSecret + comma-separated AdditionalAppSecrets.
        // Entries are trimmed, empties dropped, de-duplicated. A webhook signed by ANY of
        // our Meta apps is accepted — each candidate is a full HMAC-SHA256 + fixed-time compare.
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_options.AppSecret))
            candidates.Add(_options.AppSecret.Trim());
        if (!string.IsNullOrWhiteSpace(_options.AdditionalAppSecrets))
            candidates.AddRange(_options.AdditionalAppSecrets
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        candidates = candidates.Distinct().ToList();

        if (candidates.Count == 0)
        {
            _logger.LogError("SIGNATURE VALIDATION FAIL-CLOSED: no app secrets configured — rejecting request");
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

        var expectedBytes = Encoding.UTF8.GetBytes(signature[prefix.Length..].ToLowerInvariant());
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        // Accept if the signature matches ANY configured secret.
        foreach (var secret in candidates)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var computedHex = Convert.ToHexString(hmac.ComputeHash(payloadBytes)).ToLowerInvariant();
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(computedHex), expectedBytes))
                return true;
        }

        _logger.LogWarning("Signature validation failed");
        return false;
    }
}
