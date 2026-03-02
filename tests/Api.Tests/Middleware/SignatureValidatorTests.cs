using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Infrastructure.WhatsApp;

namespace WhatsAppSaaS.Api.Tests.Middleware;

public class SignatureValidatorTests
{
    private const string TestAppSecret = "test-app-secret-key";

    private readonly SignatureValidator _sut;

    public SignatureValidatorTests()
    {
        var options = Options.Create(new WhatsAppOptions
        {
            AppSecret = TestAppSecret,
            VerifyToken = "test",
            AccessToken = "test",
            PhoneNumberId = "123"
        });

        var logger = new Mock<ILogger<SignatureValidator>>();
        _sut = new SignatureValidator(options, logger.Object);
    }

    [Fact]
    public void IsValid_WithCorrectSignature_ReturnsTrue()
    {
        var payload = """{"object":"whatsapp_business_account","entry":[]}""";
        var signature = ComputeSignature(payload, TestAppSecret);

        _sut.IsValid(payload, signature).Should().BeTrue();
    }

    [Fact]
    public void IsValid_WithIncorrectSignature_ReturnsFalse()
    {
        var payload = """{"object":"whatsapp_business_account","entry":[]}""";

        _sut.IsValid(payload, "sha256=0000000000000000000000000000000000000000000000000000000000000000")
            .Should().BeFalse();
    }

    [Fact]
    public void IsValid_WithEmptySignature_ReturnsFalse()
    {
        _sut.IsValid("payload", "").Should().BeFalse();
    }

    [Fact]
    public void IsValid_WithMissingPrefix_ReturnsFalse()
    {
        _sut.IsValid("payload", "noprefixhere").Should().BeFalse();
    }

    [Fact]
    public void IsValid_WithNoAppSecret_ReturnsFalse()
    {
        var options = Options.Create(new WhatsAppOptions
        {
            AppSecret = "",
            VerifyToken = "test",
            AccessToken = "test",
            PhoneNumberId = "123"
        });

        var logger = new Mock<ILogger<SignatureValidator>>();
        var validator = new SignatureValidator(options, logger.Object);

        validator.IsValid("payload", "sha256=abc").Should().BeFalse();
    }

    private static string ComputeSignature(string payload, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(payloadBytes);
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
