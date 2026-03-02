using System.ComponentModel.DataAnnotations;

namespace WhatsAppSaaS.Application.Common;

public sealed class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    [Required]
    public string VerifyToken { get; set; } = string.Empty;

    [Required]
    public string AccessToken { get; set; } = string.Empty;

    [Required]
    public string PhoneNumberId { get; set; } = string.Empty;

    public string ApiVersion { get; set; } = "v21.0";

    public string? AppSecret { get; set; }

    public bool RequireSignatureValidation { get; set; } = false;
}
