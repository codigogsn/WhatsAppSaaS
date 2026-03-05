namespace WhatsAppSaaS.Domain.Entities;

public sealed class OutgoingMessage
{
    public required string To { get; init; }
    public required string Body { get; init; }
    public required string PhoneNumberId { get; init; }
    public string? AccessToken { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
