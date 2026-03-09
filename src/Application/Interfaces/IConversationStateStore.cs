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

    public bool DeliveryDataConfirmed { get; set; }
    public bool GpsPinReceived { get; set; }
    public string? LocationText { get; set; }

    public bool PaymentEvidenceRequested { get; set; }
    public bool PaymentEvidenceReceived { get; set; }
    public string? PaymentProofMediaId { get; set; }

    // Post-confirm proof capture: tracks the last confirmed order that still needs proof
    public Guid? LastOrderId { get; set; }
    public bool AwaitingPostConfirmProof { get; set; }

    public string? SpecialInstructions { get; set; }
    public bool OrderConfirmed { get; set; }
    public bool ExtrasOffered { get; set; }
    public bool ObservationPromptSent { get; set; }
    public bool ObservationAnswered { get; set; }
    public bool HumanHandoffRequested { get; set; }
    public DateTime? HumanHandoffAtUtc { get; set; }
    public int HumanHandoffNotifiedCount { get; set; }

    public bool UpsellSent { get; set; }
    public bool ComboSuggestionSent { get; set; }
    public bool SuggestionDeclined { get; set; }
    public string? LastSuggestedItem { get; set; }
    public bool AddonSuggestionSent { get; set; }

    // Analytics counters (lightweight, per-order)
    public int UpsellSuggestedCount { get; set; }
    public int UpsellAcceptedCount { get; set; }
    public int UpsellDeclinedCount { get; set; }
    public int ComboSuggestedCount { get; set; }
    public int ComboAcceptedCount { get; set; }
    public int ComboDeclinedCount { get; set; }

    public DateTime? LastActivityUtc { get; set; }

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
        DeliveryDataConfirmed = false;
        GpsPinReceived = false;
        LocationText = null;
        PaymentEvidenceRequested = false;
        PaymentEvidenceReceived = false;
        PaymentProofMediaId = null;
        // Note: LastOrderId and AwaitingPostConfirmProof are NOT reset here —
        // they allow post-confirm proof capture for pago_movil/divisas orders.
        SpecialInstructions = null;
        OrderConfirmed = false;
        ExtrasOffered = false;
        ObservationPromptSent = false;
        ObservationAnswered = false;
        HumanHandoffRequested = false;
        HumanHandoffAtUtc = null;
        HumanHandoffNotifiedCount = 0;
        UpsellSent = false;
        ComboSuggestionSent = false;
        SuggestionDeclined = false;
        LastSuggestedItem = null;
        AddonSuggestionSent = false;
        UpsellSuggestedCount = 0;
        UpsellAcceptedCount = 0;
        UpsellDeclinedCount = 0;
        ComboSuggestedCount = 0;
        ComboAcceptedCount = 0;
        ComboDeclinedCount = 0;
        LastActivityUtc = null;
    }
}

public sealed class ConversationItemEntry
{
    public string Name { get; set; } = "";
    public int Quantity { get; set; }
    public string? Modifiers { get; set; }
    public decimal UnitPrice { get; set; }
}

public interface IConversationStateStore
{
    Task<ConversationFields> GetOrCreateAsync(string conversationId, Guid? businessId, CancellationToken ct = default);
    Task SaveAsync(string conversationId, ConversationFields fields, CancellationToken ct = default);
    Task<bool> IsMessageProcessedAsync(string conversationId, string messageId, CancellationToken ct = default);
    Task MarkMessageProcessedAsync(string conversationId, string messageId, CancellationToken ct = default);
    Task PurgeOldStatesAsync(TimeSpan ttl, CancellationToken ct = default);
}
