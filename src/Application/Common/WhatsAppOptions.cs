using System.ComponentModel.DataAnnotations;

namespace WhatsAppSaaS.Application.Common;

public sealed class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    public string VerifyToken { get; set; } = string.Empty;

    // Not [Required] — can be provided via env var WHATSAPP_ACCESS_TOKEN or per-Business in DB
    public string AccessToken { get; set; } = string.Empty;

    public string PhoneNumberId { get; set; } = string.Empty;

    public string ApiVersion { get; set; } = "v21.0";

    public string? AppSecret { get; set; }

    public bool RequireSignatureValidation { get; set; } = false;
}

public sealed class PaymentMobileOptions
{
    public const string SectionName = "PaymentMobile";

    public string Bank { get; set; } = "";
    public string Id { get; set; } = "";
    public string Phone { get; set; } = "";
}
