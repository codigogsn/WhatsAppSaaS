using FluentAssertions;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Application.Services;

namespace WhatsAppSaaS.Api.Tests.Conversation;

/// <summary>
/// Tests for the cash change-return sub-flow state machine:
/// - auto-continue to payout prompt after change calculation
/// - partial checkout field completion
/// - location → finalize terminal transition
/// - exact payment skips payout
/// </summary>
public class CashFlowStateTests
{
    public CashFlowStateTests()
    {
        WebhookProcessor.ActiveCatalog = TestCatalogHelper.MenuCatalogWithExtras;
    }

    // ═══════════════════════════════════════════════════════════
    //  TEST A — Cash change auto-continue: payout prompt sent immediately
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void BuildOrderReplyFromState_CashWithChange_AwaitingPayout_ReturnPayoutPrompt()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        state.OrderConfirmed = true;
        state.DeliveryType = "delivery";
        state.PaymentMethod = "efectivo";
        state.CashCurrency = "USD";
        state.CashTenderedAmount = 20m;
        state.CashChangeRequired = true;
        state.CashChangeAmount = 7m;
        state.CashChangeAmountBs = 3100m;
        state.AwaitingCashPayout = true;

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // Should return payout data prompt since AwaitingCashPayout is true
        reply.Body.Should().Contain("Banco");
        reply.Body.Should().Contain("dula"); // Cédula
    }

    // ═══════════════════════════════════════════════════════════
    //  TEST B — After cash payout, checkout form asks only missing fields
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void BuildOrderReplyFromState_CashCompleted_WithoutCheckoutForm_SendsForm()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        state.OrderConfirmed = true;
        state.DeliveryType = "delivery";
        state.PaymentMethod = "efectivo";
        state.CashFlowCompleted = true;
        // No checkout form sent yet

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // Should send checkout form
        reply.Body.Should().Contain("Nombre:");
        state.CheckoutFormSent.Should().BeTrue();
    }

    [Fact]
    public void BuildOrderReplyFromState_CashCompleted_WithCheckoutFormSent_ShowsDataReceived()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        state.OrderConfirmed = true;
        state.DeliveryType = "delivery";
        state.PaymentMethod = "efectivo";
        state.CashFlowCompleted = true;
        state.CheckoutFormSent = true;

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // Should show data-received prompt with buttons
        reply.Body.Should().Contain("Datos recibidos");
        reply.Buttons.Should().NotBeNull();
    }

    // ═══════════════════════════════════════════════════════════
    //  TEST D — Exact cash payment skips payout
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void BuildOrderReplyFromState_CashExactPayment_SkipsPayout_GoesToCheckout()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        state.OrderConfirmed = true;
        state.DeliveryType = "delivery";
        state.PaymentMethod = "efectivo";
        state.CashCurrency = "USD";
        state.CashTenderedAmount = 6.50m;
        state.CashChangeRequired = false;
        state.CashFlowCompleted = true;

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // Should go directly to checkout form (no payout data needed)
        reply.Body.Should().Contain("Nombre:");
        state.CheckoutFormSent.Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════
    //  Cash amount parsing
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("20", 20)]
    [InlineData("20.50", 20.50)]
    [InlineData("20,50", 20.50)]
    [InlineData("20 dolares", 20)]
    [InlineData("$15", 15)]
    [InlineData("100bs", 100)]
    public void TryParseCashAmount_ValidInputs(string input, decimal expected)
    {
        var result = WebhookProcessor.TryParseCashAmount(input);
        result.Should().NotBeNull();
        result!.Value.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hola")]
    [InlineData("abc")]
    public void TryParseCashAmount_InvalidInputs(string input)
    {
        var result = WebhookProcessor.TryParseCashAmount(input);
        result.Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════
    //  FX conversion helpers
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ConvertUsdToCurrency_Bs_UsesRate()
    {
        var rate = new ResolvedRate(44.83m, "USD", DateTime.UtcNow, false);
        var result = WebhookProcessor.ConvertUsdToCurrency(10m, "Bs", rate);
        result.Should().Be(448.30m);
    }

    [Fact]
    public void ConvertUsdToCurrency_USD_NoConversion()
    {
        var rate = new ResolvedRate(44.83m, "USD", DateTime.UtcNow, false);
        var result = WebhookProcessor.ConvertUsdToCurrency(10m, "USD", rate);
        result.Should().Be(10m);
    }

    [Fact]
    public void ConvertChangeToBolivares_USD_UsesRate()
    {
        var rate = new ResolvedRate(44.83m, "USD", DateTime.UtcNow, false);
        var result = WebhookProcessor.ConvertChangeToBolivares(0.50m, "USD", rate);
        result.Should().Be(22.42m); // 0.50 * 44.83 rounded
    }

    [Fact]
    public void ComputeOrderTotalUsd_SumsItems()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "A", Quantity = 2, UnitPrice = 6.50m });
        state.Items.Add(new ConversationItemEntry { Name = "B", Quantity = 1, UnitPrice = 3.00m });

        var total = WebhookProcessor.ComputeOrderTotalUsd(state);
        total.Should().Be(16m);
    }
}
