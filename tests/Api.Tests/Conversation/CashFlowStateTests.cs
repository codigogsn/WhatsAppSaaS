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

    // ═══════════════════════════════════════════════════════════
    //  WebhookLocation DTO deserialization
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void WebhookLocation_Deserializes_Correctly()
    {
        var json = """{"latitude":10.5,"longitude":-66.9,"name":"Test","address":"Addr"}""";
        var loc = System.Text.Json.JsonSerializer.Deserialize<WhatsAppSaaS.Application.DTOs.WebhookLocation>(json);
        loc.Should().NotBeNull();
        loc!.Latitude.Should().BeApproximately(10.5, 0.01);
        loc.Longitude.Should().BeApproximately(-66.9, 0.01);
    }

    [Fact]
    public void WebhookMessage_WithLocation_HasLocationProperty()
    {
        var json = """{"from":"123","id":"m1","type":"location","timestamp":"1234","location":{"latitude":10.5,"longitude":-66.9}}""";
        var msg = System.Text.Json.JsonSerializer.Deserialize<WhatsAppSaaS.Application.DTOs.WebhookMessage>(json);
        msg.Should().NotBeNull();
        msg!.Type.Should().Be("location");
        msg.Location.Should().NotBeNull();
        msg.Location!.Latitude.Should().BeApproximately(10.5, 0.01);
    }

    // ═══════════════════════════════════════════════════════════
    //  Location finalization state tests
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void BuildOrderReplyFromState_AllFieldsComplete_WithGps_ReturnsDataReceived()
    {
        // Simulates state after cash flow + customer data + GPS — should be ready for final confirm
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        state.OrderConfirmed = true;
        state.DeliveryType = "delivery";
        state.PaymentMethod = "efectivo";
        state.CashFlowCompleted = true;
        state.CheckoutFormSent = true;
        state.CustomerName = "Juan";
        state.CustomerIdNumber = "12345678";
        state.CustomerPhone = "04141234567";
        state.Address = "Calle 1";
        state.GpsPinReceived = true;
        state.LocationText = "10.5,-66.9";
        state.DeliveryDataConfirmed = true;

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);
        // All data present — should show confirm/edit for final confirmation
        reply.Body.Should().Contain("Datos recibidos");
    }

    [Fact]
    public void BuildOrderReplyFromState_DeliveryWithoutGps_StillShowsCheckoutForm()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        state.OrderConfirmed = true;
        state.DeliveryType = "delivery";
        state.PaymentMethod = "efectivo";
        state.CashFlowCompleted = true;
        // CheckoutFormSent not set — should send checkout form
        state.GpsPinReceived = false;

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);
        reply.Body.Should().Contain("Nombre:");
        state.CheckoutFormSent.Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════
    //  POST-PAYOUT TRANSITION — no premature buttons
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void BuildOrderReplyFromState_AfterCashPayoutCompleted_ReturnsCheckoutForm()
    {
        // Simulates the state right after payout data is accepted and CashFlowCompleted=true
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
        state.CashPayoutBank = "Venezuela";
        state.CashPayoutIdNumber = "26063230";
        state.CashPayoutPhone = "04141627985";
        state.AwaitingCashPayout = false;
        state.CashFlowCompleted = true;
        // CheckoutFormSent NOT yet true — BuildOrderReplyFromState should set it

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // Should return the checkout form (Nombre/Cédula/Teléfono/Dirección)
        reply.Body.Should().Contain("Nombre:");
        state.CheckoutFormSent.Should().BeTrue();
        // Should NOT contain "Datos de vuelto recibidos" — that's the payout confirmation
        reply.Body.Should().NotContain("Datos de vuelto recibidos");
        // Should NOT have Confirmar/Editar buttons — checkout form is text-only
        reply.Buttons.Should().BeNull();
    }

    [Fact]
    public void BuildOrderReplyFromState_CashPayoutCompleted_CheckoutFormAlreadySent_ShowsDataReceived()
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
        // Customer data already filled
        state.CustomerName = "Juan";
        state.CustomerIdNumber = "12345";
        state.CustomerPhone = "04141234567";
        state.Address = "Calle 1";

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // Should show the REAL "Datos recibidos" with confirm/edit buttons
        reply.Body.Should().Contain("Datos recibidos");
        reply.Buttons.Should().NotBeNull();
        reply.Buttons.Should().HaveCountGreaterThan(0);
    }

    // ═══════════════════════════════════════════════════════════
    //  PREMATURE CONFIRM GUARD
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void BuildOrderReplyFromState_CheckoutFormSent_NoCustomerData_ReturnsDataReceivedNotMissing()
    {
        // Simulates: CheckoutFormSent=true but NO customer fields filled
        // (user tapped premature Confirmar before entering data)
        // BuildOrderReplyFromState should return "Datos recibidos" (the default post-checkout state)
        // The confirm handler separately guards against this scenario.
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        state.OrderConfirmed = true;
        state.DeliveryType = "delivery";
        state.PaymentMethod = "efectivo";
        state.CashFlowCompleted = true;
        state.CheckoutFormSent = true;
        // ALL customer fields are null

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // BuildOrderReplyFromState returns "Datos recibidos" because CheckoutFormSent=true
        reply.Body.Should().Contain("Datos recibidos");
    }

    // ═══════════════════════════════════════════════════════════
    //  SINGLE "DATOS RECIBIDOS" — no duplicates
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void CashPayoutCompleted_BuildOrderReply_OnlyCheckoutForm_NoPayoutConfirm()
    {
        // After payout data completion, BuildOrderReplyFromState should only return
        // the checkout form — no separate payout confirmation with buttons.
        // This validates the flow produces exactly one prompt, not two.
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        state.OrderConfirmed = true;
        state.DeliveryType = "delivery";
        state.PaymentMethod = "efectivo";
        state.CashFlowCompleted = true;
        // CheckoutFormSent NOT set

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // Must be the checkout form, not a payout confirmation
        reply.Body.Should().Contain("Nombre:");
        reply.Body.Should().NotContain("vuelto");
        // No interactive buttons — checkout form is text-only (buttons come later after data is filled)
        reply.Buttons.Should().BeNull();
    }
}
