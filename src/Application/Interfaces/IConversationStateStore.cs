using System;
using System.Threading;
using System.Threading.Tasks;

namespace WhatsAppSaaS.Application.Interfaces;

public sealed class ConversationFields
{
    public bool MenuSent { get; set; }

    public List<ConversationItemEntry> Items { get; set; } = new();
    public string? DeliveryType { get; set; }

    public bool CheckoutFormSent { get; set; }

    public string? CustomerName { get; set; }
    public string? CustomerIdNumber { get; set; }
    public string? CustomerPhone { get; set; }
    public string? Address { get; set; }
    public string? PaymentMethod { get; set; }

    public bool GpsPinReceived { get; set; }
    public string? LocationText { get; set; }

    public bool PaymentEvidenceRequested { get; set; }
    public bool PaymentEvidenceReceived { get; set; }

    public string? SpecialInstructions { get; set; }
    public bool ObservationPromptSent { get; set; }
    public bool ObservationAnswered { get; set; }
    public bool HumanHandoffRequested { get; set; }

    public void ResetAfterConfirm()
    {
        MenuSent = false;
        Items.Clear();
        DeliveryType = null;
        CheckoutFormSent = false;
        CustomerName = null;
        CustomerIdNumber = null;
        CustomerPhone = null;
        Address = null;
        PaymentMethod = null;
        GpsPinReceived = false;
        LocationText = null;
        PaymentEvidenceRequested = false;
        PaymentEvidenceReceived = false;
        SpecialInstructions = null;
        ObservationPromptSent = false;
        ObservationAnswered = false;
        HumanHandoffRequested = false;
    }
}

public sealed class ConversationItemEntry
{
    public string Name { get; set; } = "";
    public int Quantity { get; set; }
}

public interface IConversationStateStore
{
    Task<ConversationFields> GetOrCreateAsync(string conversationId, Guid? businessId, CancellationToken ct = default);
    Task SaveAsync(string conversationId, ConversationFields fields, CancellationToken ct = default);
    Task<bool> IsMessageProcessedAsync(string conversationId, string messageId, CancellationToken ct = default);
    Task MarkMessageProcessedAsync(string conversationId, string messageId, CancellationToken ct = default);
    Task PurgeOldStatesAsync(TimeSpan ttl, CancellationToken ct = default);
}
