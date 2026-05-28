using FluentAssertions;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Application.Services;

namespace WhatsAppSaaS.Api.Tests.Services;

// Regression: on the human-handoff path the intake parser writes customer
// fields into state.OperatorDraft, not into the legacy state.* slots used
// by the bot checkout form. BuildPendingFieldsList must prefer the draft
// before falling back to state, otherwise it falsely reports
// "missing teléfono" for a draft that already has a phone.
public sealed class HandoffPendingFieldsValidatorTests
{
    [Fact]
    public void BuildPendingFieldsList_PrefersOperatorDraftPhoneOverLegacyState()
    {
        var state = new ConversationFields
        {
            CustomerPhone = null,
            OperatorDraft = new OperatorDraft
            {
                CustomerName = "Arianna Guardia",
                CustomerIdNumber = "26809026",
                CustomerPhone = "04241521704",
                Address = "hatillo suite 2",
                DeliveryType = "delivery",
                PaymentMethod = "pago_movil"
            }
        };

        var missing = WebhookProcessor.BuildPendingFieldsList(state);

        missing.Should().NotContain(s => s.Contains("Teléfono"));
        missing.Should().NotContain(s => s.Contains("Nombre"));
        missing.Should().NotContain(s => s.Contains("Cédula"));
        missing.Should().NotContain(s => s.Contains("Dirección"));
        missing.Should().NotContain(s => s.Contains("Pago"));
        missing.Should().BeEmpty();
    }

    [Fact]
    public void BuildPendingFieldsList_FallsBackToLegacyStateWhenDraftIsNull()
    {
        var state = new ConversationFields
        {
            CustomerName = "Botflow Customer",
            CustomerIdNumber = "12345678",
            CustomerPhone = "04141234567",
            Address = "av principal",
            DeliveryType = "delivery",
            PaymentMethod = "pago_movil",
            OperatorDraft = null
        };

        var missing = WebhookProcessor.BuildPendingFieldsList(state);

        missing.Should().BeEmpty();
    }

    [Fact]
    public void BuildPendingFieldsList_ReportsMissingWhenBothSlotsEmpty()
    {
        var state = new ConversationFields
        {
            DeliveryType = "delivery",
            OperatorDraft = new OperatorDraft
            {
                CustomerName = "Only Name",
                DeliveryType = "delivery"
            }
        };

        var missing = WebhookProcessor.BuildPendingFieldsList(state);

        missing.Should().Contain(s => s.Contains("Cédula"));
        missing.Should().Contain(s => s.Contains("Teléfono"));
        missing.Should().Contain(s => s.Contains("Dirección"));
        missing.Should().Contain(s => s.Contains("Pago"));
        missing.Should().NotContain(s => s.Contains("Nombre"));
    }
}
