using FluentAssertions;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Application.Services;

namespace WhatsAppSaaS.Api.Tests.Services;

public class BrainIntentResolutionTests
{
    private static ConversationFields Fresh() => new();
    private static ConversationFields WithClarification() => new() { AwaitingBrainClarification = true };
    private static ConversationFields WithCart() => new()
    {
        Items = new() { new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1 } }
    };

    [Theory]
    [InlineData("hola bro dame 2 hamburguesas", "text", "Order")]
    [InlineData("hola quiero hablar con alguien", "text", "HumanRequest")]
    [InlineData("buenas cuanto cuesta la hamburguesa clasica", "text", "Question")]
    [InlineData("humano por favor quiero 2 pepitos", "text", "HumanRequest")]
    [InlineData("hola", "text", "Greeting")]
    [InlineData("buenas tardes", "text", "Greeting")]
    [InlineData("quiero 3 hamburguesas", "text", "Order")]
    [InlineData("2 cocacolas", "text", "Order")]
    public void ClassifyBrainIntent_FreshState_ResolvesCorrectly(string input, string msgType, string expected)
    {
        var t = input.Trim().ToLowerInvariant();
        var (intent, _) = WebhookProcessor.ClassifyBrainIntent(t, input, msgType, Fresh());
        intent.ToString().Should().Be(expected);
    }

    [Fact]
    public void ClassifyBrainIntent_AwaitingClarification_Si_ResolvesToConfirm()
    {
        var (intent, reason) = WebhookProcessor.ClassifyBrainIntent("si", "sí", "text", WithClarification());
        intent.Should().Be(WebhookProcessor.BrainIntent.ClarificationConfirm);
        reason.Should().Contain("AwaitingClarification");
    }

    [Fact]
    public void ClassifyBrainIntent_AwaitingClarification_FreeText_ResolvesToCorrection()
    {
        var (intent, reason) = WebhookProcessor.ClassifyBrainIntent("no cambiame las papas", "no cámbiame las papas", "text", WithClarification());
        intent.Should().Be(WebhookProcessor.BrainIntent.ClarificationCorrect);
        reason.Should().Contain("Correction");
    }

    [Fact]
    public void ClassifyBrainIntent_HumanOverridesGreeting()
    {
        var (intent, reason) = WebhookProcessor.ClassifyBrainIntent("hola quiero hablar con alguien", "hola quiero hablar con alguien", "text", Fresh());
        intent.Should().Be(WebhookProcessor.BrainIntent.HumanRequest);
        reason.Should().Contain("HumanOverrides");
    }

    [Fact]
    public void ClassifyBrainIntent_OrderOverridesGreeting()
    {
        var (intent, reason) = WebhookProcessor.ClassifyBrainIntent("hola bro dame 2 hamburguesas", "hola bro dame 2 hamburguesas", "text", Fresh());
        intent.Should().Be(WebhookProcessor.BrainIntent.Order);
        reason.Should().Contain("OrderOverrides");
    }

    [Fact]
    public void ClassifyBrainIntent_QuestionOverridesGreeting()
    {
        var (intent, reason) = WebhookProcessor.ClassifyBrainIntent("buenas cuanto cuesta la hamburguesa", "buenas cuánto cuesta la hamburguesa", "text", Fresh());
        intent.Should().Be(WebhookProcessor.BrainIntent.Question);
        reason.Should().Contain("QuestionOverrides");
    }

    [Fact]
    public void ClassifyBrainIntent_ActiveCart_UnknownTextBecomesOrder()
    {
        var (intent, reason) = WebhookProcessor.ClassifyBrainIntent("eso nada mas", "eso nada más", "text", WithCart());
        intent.Should().Be(WebhookProcessor.BrainIntent.Order);
        reason.Should().Contain("ActiveCart");
    }

    [Fact]
    public void ClassifyBrainIntent_GarbageText_ReturnsUnknown()
    {
        var (intent, _) = WebhookProcessor.ClassifyBrainIntent("asdfghjkl", "asdfghjkl", "text", Fresh());
        intent.Should().Be(WebhookProcessor.BrainIntent.Unknown);
    }
}
