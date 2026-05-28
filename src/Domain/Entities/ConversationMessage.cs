using System;

namespace WhatsAppSaaS.Domain.Entities;

// Durable per-message log. Append-only. Independent lifetime from
// ConversationState (which is purged after 6h). Direction/Sender/Kind
// are stored as short strings rather than enums to keep migrations
// trivial and to make ad-hoc SQL queries readable.
public sealed class ConversationMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Tenant scope. Indexed.
    public Guid BusinessId { get; set; }

    // Matches ConversationState PK convention "{from}:{phoneNumberId}".
    // Not a FK — ConversationState rows are purged at 6h TTL but messages
    // must survive independently.
    public string ConversationId { get; set; } = default!;

    // Denormalized for cross-conversation customer lookups (P3+).
    public string? CustomerPhoneE164 { get; set; }

    // WhatsApp message id for inbound; null for bot outbound (Meta does not
    // expose its own outbound id back to the sender API in the synchronous
    // response). Unique per (BusinessId, WhatsAppMessageId) when present.
    public string? WhatsAppMessageId { get; set; }

    // "inbound" | "outbound"
    public string Direction { get; set; } = "inbound";

    // "customer" | "bot" | "operator" | "system"
    public string Sender { get; set; } = "customer";

    // "text" | "image" | "document" | "location" | "interactive" | "audio"
    public string Kind { get; set; } = "text";

    public string? Body { get; set; }
    public string? MediaId { get; set; }
    public string? MimeType { get; set; }

    // Raw webhook payload JSON for inbound only. Retention-gated.
    public string? RawPayloadJson { get; set; }

    // For bot outbound: which message template was used (free-form tag).
    public string? TemplateName { get; set; }

    // Snapshot of ConversationFields.HumanOverride at the moment of write.
    public bool HandoffMode { get; set; }

    public DateTime ReceivedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
