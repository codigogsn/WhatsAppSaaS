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
    private readonly List<OutgoingMessage> _sentMessages = new();

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
            .Callback<OutgoingMessage, CancellationToken>((msg, _) => _sentMessages.Add(msg))
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

    // ═══════════════════════════════════════════════════════════════════
    //  A. HUMAN HANDOFF CORE
    // ═══════════════════════════════════════════════════════════════════

    // A1. "humano" sets handoff requested and makes conversation visible
    [Theory]
    [InlineData("humano")]
    [InlineData("asesor")]
    [InlineData("hablar con alguien")]
    public async Task HandoffKeyword_SetsHandoffRequested_NotHumanOverride(string keyword)
    {
        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(MakePayload(keyword), _testBusiness);

        // HumanHandoffRequested=true → visible in dashboard handoff queue
        _capturedState.HumanHandoffRequested.Should().BeTrue();
        _capturedState.HumanHandoffAtUtc.Should().NotBeNull();
        // HumanOverride should NOT be auto-set — only admin reply sets it
        _capturedState.HumanOverride.Should().BeFalse();
    }

    // A2. Bot sends acknowledgment on handoff keyword
    [Fact]
    public async Task HandoffKeyword_SendsAcknowledgment()
    {
        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(MakePayload("humano"), _testBusiness);

        _sentMessages.Should().HaveCountGreaterOrEqualTo(1);
        var body = _sentMessages[0].Body;
        (body.Contains("humano") || body.Contains("agente") || body.Contains("equipo")).Should().BeTrue();
    }

    // A4. HumanOverride=true (admin replied) → bot completely silent
    [Fact]
    public async Task HumanOverride_True_BotDoesNotRespond()
    {
        var state = new ConversationFields { HumanOverride = true, HumanOverrideAtUtc = DateTime.UtcNow };
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(MakePayload("hola quiero un combo"), _testBusiness);

        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // A5. Return-to-bot clears override and bot resumes
    [Fact]
    public async Task ReturnToBot_ClearsOverride_BotResumes()
    {
        var state = new ConversationFields { HumanOverride = true, HumanOverrideAtUtc = DateTime.UtcNow };
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // Bot is silent while override active
        await _sut.ProcessAsync(MakePayload("hola"), _testBusiness);
        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Simulate admin return-to-bot
        state.HumanOverride = false;
        state.HumanOverrideAtUtc = null;
        state.HumanHandoffRequested = false;
        state.HumanHandoffAtUtc = null;
        state.HumanHandoffNotifiedCount = 0;

        // New message from different phone → bot responds
        await _sut.ProcessAsync(MakePayload("hola", "584149999999"), _testBusiness);

        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  B. STUCK-CONVERSATION REGRESSIONS
    // ═══════════════════════════════════════════════════════════════════

    // B6. After "humano", if no staff replied yet, customer "cancelar" resets safely
    [Fact]
    public async Task HandoffRequested_NoAdminReply_CancelResetsSafely()
    {
        var state = new ConversationFields
        {
            HumanHandoffRequested = true,
            HumanHandoffAtUtc = DateTime.UtcNow,
            HumanHandoffNotifiedCount = 1,
            HumanOverride = false // No admin has replied
        };
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(MakePayload("cancelar"), _testBusiness);

        // Should have sent cancel confirmation
        _sentMessages.Any(m => m.Body.Contains("cancelad", StringComparison.OrdinalIgnoreCase)
            || m.Body.Contains("cancelado", StringComparison.OrdinalIgnoreCase)).Should().BeTrue();
        // Handoff flags should be cleared by ResetAfterConfirm
        _capturedState.HumanHandoffRequested.Should().BeFalse();
    }

    // B7. After "humano", if no staff replied yet, customer "hola que tal" restarts safely
    [Fact]
    public async Task HandoffRequested_NoAdminReply_GreetingRestartsSafely()
    {
        var state = new ConversationFields
        {
            HumanHandoffRequested = true,
            HumanHandoffAtUtc = DateTime.UtcNow,
            HumanHandoffNotifiedCount = 1,
            HumanOverride = false
        };
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(MakePayload("hola que tal"), _testBusiness);

        // Handoff should be cleared
        _capturedState.HumanHandoffRequested.Should().BeFalse();
        // Bot should respond with greeting (at least one message)
        _sentMessages.Should().HaveCountGreaterOrEqualTo(1);
    }

    // B8. After "humano", if no staff replied yet, customer fresh order restarts safely
    [Fact]
    public async Task HandoffRequested_NoAdminReply_OrderingIntentRestarts()
    {
        var state = new ConversationFields
        {
            HumanHandoffRequested = true,
            HumanHandoffAtUtc = DateTime.UtcNow,
            HumanHandoffNotifiedCount = 1,
            HumanOverride = false
        };
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(MakePayload("quiero pedir"), _testBusiness);

        // Handoff should be cleared, bot falls through to normal processing
        _capturedState.HumanHandoffRequested.Should().BeFalse();
        _sentMessages.Should().HaveCountGreaterOrEqualTo(1);
    }

    // B9. After admin has replied (HumanOverride=true), customer message does NOT re-enter bot
    [Fact]
    public async Task HumanOverride_True_CustomerMessageStaysSilent()
    {
        var state = new ConversationFields
        {
            HumanHandoffRequested = true,
            HumanOverride = true,
            HumanOverrideAtUtc = DateTime.UtcNow
        };
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(MakePayload("hola"), _testBusiness);
        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await _sut.ProcessAsync(MakePayload("cancelar"), _testBusiness);
        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await _sut.ProcessAsync(MakePayload("quiero pedir"), _testBusiness);
        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // B10. After admin reply, only return-to-bot restores bot control
    [Fact]
    public async Task HumanOverride_OnlyReturnToBotRestores()
    {
        var state = new ConversationFields
        {
            HumanOverride = true,
            HumanOverrideAtUtc = DateTime.UtcNow,
            HumanHandoffRequested = true
        };
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // All attempts to break out fail while HumanOverride is true
        foreach (var msg in new[] { "hola", "cancelar", "quiero pedir", "menu" })
        {
            await _sut.ProcessAsync(MakePayload(msg), _testBusiness);
        }
        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Admin returns to bot
        state.HumanOverride = false;
        state.HumanOverrideAtUtc = null;
        state.HumanHandoffRequested = false;

        await _sut.ProcessAsync(MakePayload("hola", "584149999999"), _testBusiness);
        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  C. DASHBOARD / API
    // ═══════════════════════════════════════════════════════════════════

    // C11-C14 are covered by endpoint integration (controller tests would need full DI).
    // Here we verify the state model contracts used by the API.

    [Fact]
    public void ManualReply_OutgoingMessage_ConstructedCorrectly()
    {
        var msg = new OutgoingMessage
        {
            To = "584141234567",
            Body = "Hola, soy el equipo de soporte.",
            PhoneNumberId = "123456789",
            AccessToken = "test-token"
        };
        msg.To.Should().Be("584141234567");
        msg.Body.Should().Contain("soporte");
        msg.PhoneNumberId.Should().Be("123456789");
    }

    [Fact]
    public void ConversationFields_HandoffFlags_IndependentFromOverride()
    {
        // Verifies the two flag systems are independent
        var fields = new ConversationFields
        {
            HumanHandoffRequested = true,
            HumanHandoffAtUtc = DateTime.UtcNow,
            HumanOverride = false
        };
        fields.HumanHandoffRequested.Should().BeTrue();
        fields.HumanOverride.Should().BeFalse();

        // Clearing one doesn't affect the other
        fields.HumanHandoffRequested = false;
        fields.HumanOverride.Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  D. REGRESSION SAFETY
    // ═══════════════════════════════════════════════════════════════════

    // D16. Normal greeting flow still works
    [Fact]
    public async Task NormalConversation_GreetingWorks()
    {
        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(MakePayload("hola"), _testBusiness);

        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        _capturedState.HumanHandoffRequested.Should().BeFalse();
        _capturedState.HumanOverride.Should().BeFalse();
    }

    // D17. Cancel flow still works outside handoff
    [Fact]
    public async Task NormalConversation_CancelWorks()
    {
        var state = new ConversationFields { MenuSent = true };
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(MakePayload("cancelar"), _testBusiness);

        _sentMessages.Any(m => m.Body.Contains("cancelad", StringComparison.OrdinalIgnoreCase)
            || m.Body.Contains("cancelado", StringComparison.OrdinalIgnoreCase)).Should().BeTrue();
        _capturedState.HumanHandoffRequested.Should().BeFalse();
    }

    // D19. Repeated greeting smoothing still works
    [Fact]
    public async Task NormalConversation_RepeatedGreetingSmoothing()
    {
        var state = new ConversationFields { MenuSent = true };
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(MakePayload("hola"), _testBusiness);

        // Should not break — bot handles repeated greeting on active conversation
        _capturedState.HumanOverride.Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  E. STATE DEFAULTS & INVARIANTS
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void ConversationFields_Defaults_BothFlagsFalse()
    {
        var fields = new ConversationFields();
        fields.HumanOverride.Should().BeFalse();
        fields.HumanOverrideAtUtc.Should().BeNull();
        fields.HumanHandoffRequested.Should().BeFalse();
        fields.HumanHandoffAtUtc.Should().BeNull();
    }

    [Fact]
    public void ResetAfterConfirm_ClearsHandoffRequested_PreservesHumanOverride()
    {
        var fields = new ConversationFields
        {
            HumanHandoffRequested = true,
            HumanHandoffAtUtc = DateTime.UtcNow,
            HumanHandoffNotifiedCount = 2,
            HumanOverride = true,
            HumanOverrideAtUtc = DateTime.UtcNow
        };

        fields.ResetAfterConfirm();

        // ResetAfterConfirm clears HumanHandoffRequested
        fields.HumanHandoffRequested.Should().BeFalse();
        fields.HumanHandoffAtUtc.Should().BeNull();
        fields.HumanHandoffNotifiedCount.Should().Be(0);
        // HumanOverride persists — only admin can clear it
        fields.HumanOverride.Should().BeTrue();
        fields.HumanOverrideAtUtc.Should().NotBeNull();
    }

    // Handoff waiting messages are sent (max 3) for non-escape messages
    [Fact]
    public async Task HandoffRequested_NonEscapeMessage_SendsWaitingNotification()
    {
        var state = new ConversationFields
        {
            HumanHandoffRequested = true,
            HumanHandoffAtUtc = DateTime.UtcNow,
            HumanHandoffNotifiedCount = 1,
            HumanOverride = false
        };
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // "cuanto cuesta" is not a cancel, greeting, or ordering intent
        await _sut.ProcessAsync(MakePayload("cuanto cuesta el combo"), _testBusiness);

        // Bot should send a waiting message, not process the order
        _sentMessages.Should().HaveCountGreaterOrEqualTo(1);
        // Handoff should remain active
        _capturedState.HumanHandoffRequested.Should().BeTrue();
    }
}
