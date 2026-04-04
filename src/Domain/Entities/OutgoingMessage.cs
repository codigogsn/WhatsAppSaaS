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

    /// <summary>
    /// When true, the message is sent as a WhatsApp location_request_message.
    /// Body is used as the prompt text.
    /// </summary>
    public bool LocationRequest { get; init; }

    /// <summary>
    /// When set, the message is sent as a document (PDF, etc.) via public URL.
    /// Body is used as the document caption.
    /// </summary>
    public string? DocumentUrl { get; init; }

    /// <summary>
    /// Filename shown to the recipient when DocumentUrl is set.
    /// </summary>
    public string? DocumentFilename { get; init; }

    /// <summary>
    /// Optional WhatsApp interactive list message. When set, sent as type:"list".
    /// </summary>
    public ListMessageData? ListMessage { get; init; }
}

/// <summary>
/// Data for a WhatsApp interactive list message.
/// </summary>
public sealed class ListMessageData
{
    public required string ButtonText { get; init; }
    public string? HeaderText { get; init; }
    public string? FooterText { get; init; }
    public required List<ListSection> Sections { get; init; }
}

public sealed class ListSection
{
    public required string Title { get; init; }
    public required List<ListRow> Rows { get; init; }
}

public sealed class ListRow
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// A single WhatsApp quick-reply button.
/// </summary>
public sealed record ReplyButton(string Id, string Title);
