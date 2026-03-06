namespace WhatsAppSaaS.Application.Common;

public sealed record BusinessContext(
    Guid BusinessId,
    string PhoneNumberId,
    string AccessToken,
    string BusinessName = "",
    string? PaymentMobileBank = null,
    string? PaymentMobileId = null,
    string? PaymentMobilePhone = null);
