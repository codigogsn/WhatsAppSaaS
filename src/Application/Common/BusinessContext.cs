namespace WhatsAppSaaS.Application.Common;

public sealed record BusinessContext(
    Guid BusinessId,
    string PhoneNumberId,
    string AccessToken,
    string BusinessName = "",
    string? Greeting = null,
    string? Schedule = null,
    string? Address = null,
    string? LogoUrl = null,
    string? PaymentMobileBank = null,
    string? PaymentMobileId = null,
    string? PaymentMobilePhone = null);
