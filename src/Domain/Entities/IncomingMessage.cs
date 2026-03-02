using WhatsAppSaaS.Domain.Enums;

namespace WhatsAppSaaS.Domain.Entities;

public sealed class IncomingMessage
{
    public string SenderPhoneNumber { get; init; } = string.Empty;
    public string RecipientPhoneNumberId { get; init; } = string.Empty;
    public string MessageId { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public MessageType Type { get; init; } = MessageType.Text;
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Future: tenant identifier for multi-tenant SaaS.
    /// </summary>
    public string? TenantId { get; init; }
}
