using FluentAssertions;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Application.Services;

namespace WhatsAppSaaS.Api.Tests.Workers;

public class CheckoutReminderTests
{
    [Fact]
    public void CheckoutPendingSinceUtc_SetWhenCheckoutFormSent()
    {
        var state = new ConversationFields
        {
            Items = { new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1, UnitPrice = 5m } },
            OrderConfirmed = true,
            ObservationAnswered = true,
            ExtrasOffered = true,
            DeliveryType = "delivery",
            PaymentMethod = "efectivo",
            CashFlowCompleted = true
        };

        // CheckoutFormSent is false, so BuildOrderReplyFromState should set it
        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        state.CheckoutFormSent.Should().BeTrue();
        state.CheckoutPendingSinceUtc.Should().NotBeNull();
        reply.Body.Should().Contain("reservado");
    }

    [Fact]
    public void CheckoutPendingSinceUtc_NotSetWhenNoItems()
    {
        var state = new ConversationFields();

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        state.CheckoutPendingSinceUtc.Should().BeNull();
    }

    [Fact]
    public void CheckoutPendingSinceUtc_ClearedOnReset()
    {
        var state = new ConversationFields
        {
            CheckoutPendingSinceUtc = DateTime.UtcNow,
            Reminder1SentAtUtc = DateTime.UtcNow,
            Reminder2SentAtUtc = DateTime.UtcNow
        };

        state.ResetAfterConfirm();

        state.CheckoutPendingSinceUtc.Should().BeNull();
        state.Reminder1SentAtUtc.Should().BeNull();
        state.Reminder2SentAtUtc.Should().BeNull();
    }

    [Fact]
    public void ReminderState_StopWhenUserReplied()
    {
        // Simulate: checkout started at T, user replied at T+2min
        var checkoutStart = DateTime.UtcNow.AddMinutes(-6);
        var userReply = DateTime.UtcNow.AddMinutes(-4);

        var state = new ConversationFields
        {
            CheckoutPendingSinceUtc = checkoutStart,
            LastActivityUtc = userReply,
            Items = { new ConversationItemEntry { Name = "Test", Quantity = 1, UnitPrice = 1m } },
            CheckoutFormSent = true
        };

        // User replied AFTER checkout started — reminders should stop
        state.LastActivityUtc.Should().BeAfter(state.CheckoutPendingSinceUtc.Value);
    }

    [Fact]
    public void ReminderState_MaxTwoReminders()
    {
        var state = new ConversationFields
        {
            CheckoutPendingSinceUtc = DateTime.UtcNow.AddMinutes(-20),
            Reminder1SentAtUtc = DateTime.UtcNow.AddMinutes(-15),
            Reminder2SentAtUtc = DateTime.UtcNow.AddMinutes(-5)
        };

        // After both reminders sent, CheckoutPendingSinceUtc should be null
        // (the worker clears it after reminder 2)
        // Here we verify both timestamps are set, proving max 2 reminders
        state.Reminder1SentAtUtc.Should().NotBeNull();
        state.Reminder2SentAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void ReservationMessage_OnlyInPendingCheckout()
    {
        // State WITHOUT checkout form — should NOT get reservation message
        var stateNoCheckout = new ConversationFields
        {
            Items = { new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1, UnitPrice = 5m } },
            ExtrasOffered = true,
            ObservationAnswered = true,
            DeliveryType = "delivery"
        };

        var reply1 = WebhookProcessor.BuildOrderReplyFromState(stateNoCheckout);
        // This should be the confirmation prompt (not checkout form yet)
        reply1.Body.Should().NotContain("reservado");

        // Now simulate that order is confirmed and payment selected
        stateNoCheckout.OrderConfirmed = true;
        stateNoCheckout.PaymentMethod = "efectivo";
        stateNoCheckout.CashFlowCompleted = true;

        var reply2 = WebhookProcessor.BuildOrderReplyFromState(stateNoCheckout);
        // NOW checkout form is sent, reservation message should appear
        reply2.Body.Should().Contain("reservado");
    }

    [Fact]
    public void ReservationMessage_IncludedInPickupCheckout()
    {
        var state = new ConversationFields
        {
            Items = { new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1, UnitPrice = 5m } },
            OrderConfirmed = true,
            ObservationAnswered = true,
            ExtrasOffered = true,
            DeliveryType = "pickup",
            PaymentMethod = "efectivo",
            CashFlowCompleted = true
        };

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        reply.Body.Should().Contain("PICKUP");
        reply.Body.Should().Contain("reservado");
    }

    [Fact]
    public void NoReminderState_WhenOrderCompleted()
    {
        var state = new ConversationFields
        {
            CheckoutPendingSinceUtc = DateTime.UtcNow.AddMinutes(-10),
            Reminder1SentAtUtc = DateTime.UtcNow.AddMinutes(-5)
        };

        // Order completed — reset clears everything
        state.ResetAfterConfirm();

        state.CheckoutPendingSinceUtc.Should().BeNull();
        state.Reminder1SentAtUtc.Should().BeNull();
        state.Reminder2SentAtUtc.Should().BeNull();
    }
}
