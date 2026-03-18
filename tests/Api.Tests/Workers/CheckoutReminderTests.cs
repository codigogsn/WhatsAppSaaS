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
    public void ReminderState_StopWhenUserReplied_AfterThreshold()
    {
        // User replied 2 minutes after checkout started — beyond 30s threshold
        var checkoutStart = DateTime.UtcNow.AddMinutes(-6);
        var userReply = checkoutStart.AddMinutes(2);

        var state = new ConversationFields
        {
            CheckoutPendingSinceUtc = checkoutStart,
            LastActivityUtc = userReply,
            Items = { new ConversationItemEntry { Name = "Test", Quantity = 1, UnitPrice = 1m } },
            CheckoutFormSent = true
        };

        // User replied > 30s after checkout started → should stop
        state.LastActivityUtc.Value.Should().BeAfter(state.CheckoutPendingSinceUtc.Value.AddSeconds(30));
    }

    [Fact]
    public void ReminderState_NoStopWhenSameCycleActivity()
    {
        // Activity within 5 seconds of checkout start = same processing cycle
        var checkoutStart = DateTime.UtcNow.AddMinutes(-6);
        var sameCycleActivity = checkoutStart.AddSeconds(2);

        var state = new ConversationFields
        {
            CheckoutPendingSinceUtc = checkoutStart,
            LastActivityUtc = sameCycleActivity
        };

        // Activity within 30s of checkout start → should NOT count as user reply
        state.LastActivityUtc.Value.Should().BeBefore(state.CheckoutPendingSinceUtc.Value.AddSeconds(30));
    }

    [Fact]
    public void ReminderState_MaxTwoReminders()
    {
        var state = new ConversationFields
        {
            CheckoutPendingSinceUtc = null, // Worker clears this after reminder 2
            Reminder1SentAtUtc = DateTime.UtcNow.AddMinutes(-15),
            Reminder2SentAtUtc = DateTime.UtcNow.AddMinutes(-5)
        };

        // After both reminders, CheckoutPendingSinceUtc is null (worker cleared it)
        state.CheckoutPendingSinceUtc.Should().BeNull();
        state.Reminder1SentAtUtc.Should().NotBeNull();
        state.Reminder2SentAtUtc.Should().NotBeNull();
    }

    [Fact]
    public void Reminder1Eligible_WhenElapsed5Min()
    {
        var state = new ConversationFields
        {
            CheckoutPendingSinceUtc = DateTime.UtcNow.AddMinutes(-6),
            LastActivityUtc = DateTime.UtcNow.AddMinutes(-10), // Before checkout
            Items = { new ConversationItemEntry { Name = "Burger", Quantity = 1, UnitPrice = 5m } },
            CheckoutFormSent = true
        };

        state.CheckoutPendingSinceUtc.Should().NotBeNull();
        state.Reminder1SentAtUtc.Should().BeNull(); // Not yet sent
        var elapsed = DateTime.UtcNow - state.CheckoutPendingSinceUtc!.Value;
        elapsed.TotalMinutes.Should().BeGreaterThan(5); // Eligible for reminder 1
    }

    [Fact]
    public void Reminder2Eligible_WhenElapsed15Min()
    {
        var state = new ConversationFields
        {
            CheckoutPendingSinceUtc = DateTime.UtcNow.AddMinutes(-16),
            Reminder1SentAtUtc = DateTime.UtcNow.AddMinutes(-11), // Sent at ~5 min
            LastActivityUtc = DateTime.UtcNow.AddMinutes(-20),
            Items = { new ConversationItemEntry { Name = "Burger", Quantity = 1, UnitPrice = 5m } },
            CheckoutFormSent = true
        };

        state.Reminder1SentAtUtc.Should().NotBeNull();
        state.Reminder2SentAtUtc.Should().BeNull();
        var elapsed = DateTime.UtcNow - state.CheckoutPendingSinceUtc!.Value;
        elapsed.TotalMinutes.Should().BeGreaterThan(15); // Eligible for reminder 2
    }

    [Fact]
    public void ReservationMessage_OnlyInPendingCheckout()
    {
        var stateNoCheckout = new ConversationFields
        {
            Items = { new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1, UnitPrice = 5m } },
            ExtrasOffered = true,
            ObservationAnswered = true,
            DeliveryType = "delivery"
        };

        var reply1 = WebhookProcessor.BuildOrderReplyFromState(stateNoCheckout);
        reply1.Body.Should().NotContain("reservado");

        stateNoCheckout.OrderConfirmed = true;
        stateNoCheckout.PaymentMethod = "efectivo";
        stateNoCheckout.CashFlowCompleted = true;

        var reply2 = WebhookProcessor.BuildOrderReplyFromState(stateNoCheckout);
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

        state.ResetAfterConfirm();

        state.CheckoutPendingSinceUtc.Should().BeNull();
        state.Reminder1SentAtUtc.Should().BeNull();
        state.Reminder2SentAtUtc.Should().BeNull();
    }

    [Fact]
    public void NoReminder_WhenItemsCleared()
    {
        var state = new ConversationFields
        {
            CheckoutPendingSinceUtc = DateTime.UtcNow.AddMinutes(-6),
            CheckoutFormSent = true
            // Items is empty — order was cleared/cancelled
        };

        state.Items.Count.Should().Be(0);
        state.CheckoutPendingSinceUtc.Should().NotBeNull();
        // Worker will clear this because Items.Count == 0
    }
}
