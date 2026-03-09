using System.Text.Json.Serialization;

namespace WhatsAppSaaS.Application.DTOs;

/// <summary>
/// Root payload received from WhatsApp Cloud API webhook POST.
/// Modeled after the official Meta webhook payload structure.
/// </summary>
public sealed class WebhookPayload
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = string.Empty;

    [JsonPropertyName("entry")]
    public List<WebhookEntry> Entry { get; set; } = [];
}

public sealed class WebhookEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("changes")]
    public List<WebhookChange> Changes { get; set; } = [];
}

public sealed class WebhookChange
{
    [JsonPropertyName("value")]
    public WebhookChangeValue? Value { get; set; }

    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;
}

public sealed class WebhookChangeValue
{
    [JsonPropertyName("messaging_product")]
    public string MessagingProduct { get; set; } = string.Empty;

    [JsonPropertyName("metadata")]
    public WebhookMetadata? Metadata { get; set; }

    [JsonPropertyName("contacts")]
    public List<WebhookContact>? Contacts { get; set; }

    [JsonPropertyName("messages")]
    public List<WebhookMessage>? Messages { get; set; }

    [JsonPropertyName("statuses")]
    public List<WebhookStatus>? Statuses { get; set; }
}

public sealed class WebhookMetadata
{
    [JsonPropertyName("display_phone_number")]
    public string DisplayPhoneNumber { get; set; } = string.Empty;

    [JsonPropertyName("phone_number_id")]
    public string PhoneNumberId { get; set; } = string.Empty;
}

public sealed class WebhookContact
{
    [JsonPropertyName("profile")]
    public WebhookProfile? Profile { get; set; }

    [JsonPropertyName("wa_id")]
    public string WaId { get; set; } = string.Empty;
}

public sealed class WebhookProfile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class WebhookMessage
{
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public WebhookText? Text { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("image")]
    public WebhookMedia? Image { get; set; }

    [JsonPropertyName("document")]
    public WebhookMedia? Document { get; set; }

    [JsonPropertyName("interactive")]
    public WebhookInteractive? Interactive { get; set; }
}

public sealed class WebhookInteractive
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("button_reply")]
    public WebhookButtonReply? ButtonReply { get; set; }
}

public sealed class WebhookButtonReply
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
}

public sealed class WebhookMedia
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("mime_type")]
    public string MimeType { get; set; } = string.Empty;
}

public sealed class WebhookText
{
    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;
}

public sealed class WebhookStatus
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("recipient_id")]
    public string RecipientId { get; set; } = string.Empty;
}
