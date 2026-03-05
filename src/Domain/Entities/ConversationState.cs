using System;
using System.Collections.Generic;

namespace WhatsAppSaaS.Domain.Entities;

public sealed class ConversationState
{
    // PK: "{from}:{phoneNumberId}"
    public string ConversationId { get; set; } = default!;

    public Guid? BusinessId { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    // Serialized JSON blob of the conversation fields
    public string StateJson { get; set; } = "{}";

    public List<ProcessedMessage> ProcessedMessages { get; set; } = new();
}
