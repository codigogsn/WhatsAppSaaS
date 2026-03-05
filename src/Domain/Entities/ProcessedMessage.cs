using System;

namespace WhatsAppSaaS.Domain.Entities;

public sealed class ProcessedMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string ConversationId { get; set; } = default!;

    public string MessageId { get; set; } = default!;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ConversationState? Conversation { get; set; }
}
