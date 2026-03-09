namespace WhatsAppSaaS.Domain.Entities;

public sealed class OutgoingMessage
{
    public required string To { get; init; }
    public required string Body { get; init; }
    public required string PhoneNumberId { get; init; }
    public string? AccessToken { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Optional WhatsApp reply buttons (max 3). When set, the message is sent
    /// as an interactive button message instead of plain text.
    /// </summary>
    public List<ReplyButton>? Buttons { get; init; }
}

/// <summary>
/// A single WhatsApp quick-reply button.
/// </summary>
public sealed record ReplyButton(string Id, string Title);
