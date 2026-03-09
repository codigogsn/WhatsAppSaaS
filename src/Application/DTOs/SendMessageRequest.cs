using System.Text.Json.Serialization;

namespace WhatsAppSaaS.Application.DTOs;

/// <summary>
/// Outbound message payload for the Meta Graph API.
/// Supports both plain text and interactive button messages.
/// </summary>
public sealed class SendMessageRequest
{
    [JsonPropertyName("messaging_product")]
    public string MessagingProduct { get; init; } = "whatsapp";

    [JsonPropertyName("recipient_type")]
    public string RecipientType { get; init; } = "individual";

    [JsonPropertyName("to")]
    public required string To { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SendMessageText? Text { get; init; }

    [JsonPropertyName("interactive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SendInteractivePayload? Interactive { get; init; }
}

public sealed class SendMessageText
{
    [JsonPropertyName("preview_url")]
    public bool PreviewUrl { get; init; } = false;

    [JsonPropertyName("body")]
    public required string Body { get; init; }
}

// ── Interactive button message DTOs ──

public sealed class SendInteractivePayload
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "button";

    [JsonPropertyName("body")]
    public required SendInteractiveBody Body { get; init; }

    [JsonPropertyName("action")]
    public required SendInteractiveAction Action { get; init; }
}

public sealed class SendInteractiveBody
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public sealed class SendInteractiveAction
{
    [JsonPropertyName("buttons")]
    public required List<SendInteractiveButton> Buttons { get; init; }
}

public sealed class SendInteractiveButton
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "reply";

    [JsonPropertyName("reply")]
    public required SendInteractiveReply Reply { get; init; }
}

public sealed class SendInteractiveReply
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }
}
