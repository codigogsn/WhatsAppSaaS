using System.Text.Json.Serialization;

namespace WhatsAppSaaS.Application.DTOs;

/// <summary>
/// Outbound message payload for the Meta Graph API.
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
    public required SendMessageText Text { get; init; }
}

public sealed class SendMessageText
{
    [JsonPropertyName("preview_url")]
    public bool PreviewUrl { get; init; } = false;

    [JsonPropertyName("body")]
    public required string Body { get; init; }
}
