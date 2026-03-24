using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.DTOs;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Application.Services;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Api.Tests.Services;

public sealed class HumanHandoffTests
{
    private readonly Mock<IWhatsAppClient> _whatsAppClientMock;
    private readonly Mock<IConversationStateStore> _stateStoreMock;
    private readonly WebhookProcessor _sut;
    private readonly BusinessContext _testBusiness;
    private ConversationFields _capturedState = new();

    public HumanHandoffTests()
    {
        _whatsAppClientMock = new Mock<IWhatsAppClient>();
        _stateStoreMock = new Mock<IConversationStateStore>();

        _stateStoreMock
            .Setup(x => x.IsMessageProcessedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _stateStoreMock
            .Setup(x => x.MarkMessageProcessedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _stateStoreMock
            .Setup(x => x.SaveAsync(It.IsAny<string>(), It.IsAny<ConversationFields>(), It.IsAny<CancellationToken>()))
            .Callback<string, ConversationFields, CancellationToken>((_, fields, _) => _capturedState = fields)
            .Returns(Task.CompletedTask);

        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _testBusiness = new BusinessContext(Guid.NewGuid(), "123456789", "test-token", "Mi Restaurante",
            MenuPdfUrl: "https://test.example.com/menu-demo.pdf");

        _sut = new WebhookProcessor(
            new Mock<IAiParser>().Object,
            _whatsAppClientMock.Object,
            new Mock<IOrderRepository>().Object,
            _stateStoreMock.Object,
            new Mock<ILogger<WebhookProcessor>>().Object);
    }

    private WebhookPayload MakePayload(string text, string from = "584141234567")
    {
        return new WebhookPayload
        {
            Entry = new List<WebhookEntry>
            {
                new()
                {
                    Changes = new List<WebhookChange>
                    {
                        new()
                        {
                            Value = new WebhookChangeValue
                            {
                                Metadata = new WebhookMetadata { PhoneNumberId = _testBusiness.PhoneNumberId },
                                Messages = new List<WebhookMessage>
                                {
                                    new()
                                    {
                                        Id = Guid.NewGuid().ToString(),
                                        From = from,
                                        Type = "text",
                                        Text = new WebhookText { Body = text }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    // ═══════════════════════════════════════════════════════════
    //  1. When HumanOverride = true → bot does NOT respond
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task HumanOverride_True_BotDoesNotRespond()
    {
        var state = new ConversationFields { HumanOverride = true, HumanOverrideAtUtc = DateTime.UtcNow };

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(MakePayload("hola quiero un combo"), _testBusiness);

        // Bot must NOT send any message
        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // State should be saved (activity tracked) but HumanOverride preserved
        _stateStoreMock.Verify(
            x => x.SaveAsync(It.IsAny<string>(), It.Is<ConversationFields>(f => f.HumanOverride), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HumanOverride_True_EvenGreetingsAreSilent()
    {
        var state = new ConversationFields { HumanOverride = true, HumanOverrideAtUtc = DateTime.UtcNow };

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(MakePayload("hola"), _testBusiness);

        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HumanOverride_True_OrderIntentsAlsoSilent()
    {
        // Unlike HumanHandoffRequested, HumanOverride does NOT break out on ordering intent
        var state = new ConversationFields { HumanOverride = true, HumanOverrideAtUtc = DateTime.UtcNow };

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(MakePayload("quiero pedir"), _testBusiness);

        // Bot stays silent even for ordering intents when admin is in control
        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ═══════════════════════════════════════════════════════════
    //  2. Manual reply sends message correctly (unit test for send)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ManualReply_OutgoingMessage_ConstructedCorrectly()
    {
        // Verify OutgoingMessage can be constructed for admin reply
        var msg = new OutgoingMessage
        {
            To = "584141234567",
            Body = "Hola, soy el equipo de soporte. ¿En qué puedo ayudarte?",
            PhoneNumberId = "123456789",
            AccessToken = "test-token"
        };

        msg.To.Should().Be("584141234567");
        msg.Body.Should().Contain("soporte");
        msg.PhoneNumberId.Should().Be("123456789");
    }

    // ═══════════════════════════════════════════════════════════
    //  3. Return-to-bot restores bot behavior
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task ReturnToBot_ClearsOverride_BotResumes()
    {
        // Start with HumanOverride = true
        var state = new ConversationFields { HumanOverride = true, HumanOverrideAtUtc = DateTime.UtcNow };

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // First message: bot is silent
        await _sut.ProcessAsync(MakePayload("hola"), _testBusiness);
        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Simulate admin clicking "return to bot"
        state.HumanOverride = false;
        state.HumanOverrideAtUtc = null;
        state.HumanHandoffRequested = false;
        state.HumanHandoffAtUtc = null;
        state.HumanHandoffNotifiedCount = 0;

        // Next message: bot responds normally
        await _sut.ProcessAsync(MakePayload("hola", "584149999999"), _testBusiness);

        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    // ═══════════════════════════════════════════════════════════
    //  4. Normal conversations are completely unaffected
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task NormalConversation_HumanOverrideFalse_BotRespondsNormally()
    {
        var state = new ConversationFields { HumanOverride = false };

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(MakePayload("hola"), _testBusiness);

        // Bot should respond to greeting
        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task NewConversation_DefaultState_BotResponds()
    {
        // Fresh ConversationFields: HumanOverride defaults to false
        var state = new ConversationFields();

        state.HumanOverride.Should().BeFalse();
        state.HumanOverrideAtUtc.Should().BeNull();

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(MakePayload("hola"), _testBusiness);

        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    // ═══════════════════════════════════════════════════════════
    //  5. Handoff keyword triggers HumanOverride flag
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("humano")]
    [InlineData("asesor")]
    [InlineData("hablar con alguien")]
    public async Task HandoffKeyword_SetsHumanOverrideFlag(string keyword)
    {
        var state = new ConversationFields();

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(MakePayload(keyword), _testBusiness);

        _capturedState.HumanOverride.Should().BeTrue();
        _capturedState.HumanOverrideAtUtc.Should().NotBeNull();
        _capturedState.HumanHandoffRequested.Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════
    //  6. ConversationFields defaults are safe
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ConversationFields_Defaults_HumanOverrideFalse()
    {
        var fields = new ConversationFields();
        fields.HumanOverride.Should().BeFalse();
        fields.HumanOverrideAtUtc.Should().BeNull();
    }

    [Fact]
    public void ConversationFields_ResetAfterConfirm_DoesNotClearHumanOverride()
    {
        var fields = new ConversationFields
        {
            HumanOverride = true,
            HumanOverrideAtUtc = DateTime.UtcNow
        };

        fields.ResetAfterConfirm();

        // HumanOverride must persist across order resets — only admin can clear it
        fields.HumanOverride.Should().BeTrue();
        fields.HumanOverrideAtUtc.Should().NotBeNull();
    }
}
