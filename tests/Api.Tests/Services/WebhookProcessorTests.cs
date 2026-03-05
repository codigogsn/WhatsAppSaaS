using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.DTOs;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Application.Services;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Api.Tests.Services;

public class WebhookProcessorTests
{
    private readonly Mock<IAiParser> _aiParserMock;
    private readonly Mock<IWhatsAppClient> _whatsAppClientMock;
    private readonly Mock<IOrderRepository> _orderRepositoryMock;
    private readonly Mock<IConversationStateStore> _stateStoreMock;
    private readonly WebhookProcessor _sut;
    private readonly BusinessContext _testBusiness;

    public WebhookProcessorTests()
    {
        _aiParserMock = new Mock<IAiParser>();
        _whatsAppClientMock = new Mock<IWhatsAppClient>();
        _orderRepositoryMock = new Mock<IOrderRepository>();
        _stateStoreMock = new Mock<IConversationStateStore>();

        _orderRepositoryMock
            .Setup(x => x.AddOrderAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Default: return fresh state, no duplicate messages
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationFields());

        _stateStoreMock
            .Setup(x => x.IsMessageProcessedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _stateStoreMock
            .Setup(x => x.MarkMessageProcessedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _stateStoreMock
            .Setup(x => x.SaveAsync(It.IsAny<string>(), It.IsAny<ConversationFields>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _testBusiness = new BusinessContext(Guid.NewGuid(), "123456789", "test-token");

        _sut = new WebhookProcessor(
            _aiParserMock.Object,
            _whatsAppClientMock.Object,
            _orderRepositoryMock.Object,
            _stateStoreMock.Object,
            new Mock<ILogger<WebhookProcessor>>().Object);
    }

    [Fact]
    public async Task ProcessAsync_WithTextMessage_CallsAiParserAndSendsReply()
    {
        _aiParserMock
            .Setup(x => x.ParseAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiParseResult
            {
                Intent = RestaurantIntent.OrderCreate,
                Confidence = 0.95,
                MissingFields = [],
                Args = new ParsedArgs
                {
                    Order = new OrderArgs
                    {
                        Items = [new WhatsAppSaaS.Application.DTOs.OrderItem { Name = "Hamburguesa", Quantity = 2 }],
                        DeliveryType = "pickup"
                    }
                }
            });

        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var payload = CreateTextMessagePayload("5511999999999", "quiero 2 hamburguesas para recoger");

        await _sut.ProcessAsync(payload, _testBusiness);

        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(
                It.Is<OutgoingMessage>(m =>
                    m.To == "5511999999999"),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ProcessAsync_HumanHandoff_SendsHandoffMessage()
    {
        _aiParserMock
            .Setup(x => x.ParseAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiParseResult
            {
                Intent = RestaurantIntent.HumanHandoff,
                Confidence = 0.92,
                MissingFields = [],
                Args = new ParsedArgs
                {
                    Handoff = new HandoffArgs { Reason = "queja sobre comida" }
                }
            });

        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var payload = CreateTextMessagePayload("5511999999999", "quiero hablar con un gerente");

        await _sut.ProcessAsync(payload, _testBusiness);

        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(
                It.Is<OutgoingMessage>(m => m.Body.Contains("humano")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_WithEmptyEntries_DoesNotCallAiParser()
    {
        var payload = new WebhookPayload { Object = "whatsapp_business_account", Entry = [] };

        await _sut.ProcessAsync(payload, _testBusiness);

        _aiParserMock.Verify(
            x => x.ParseAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProcessAsync_WithNonTextMessage_DoesNotCallAiParser()
    {
        var payload = new WebhookPayload
        {
            Object = "whatsapp_business_account",
            Entry =
            [
                new WebhookEntry
                {
                    Id = "entry1",
                    Changes =
                    [
                        new WebhookChange
                        {
                            Field = "messages",
                            Value = new WebhookChangeValue
                            {
                                Metadata = new WebhookMetadata { PhoneNumberId = "123" },
                                Messages =
                                [
                                    new WebhookMessage
                                    {
                                        From = "5511999999999",
                                        Id = "wamid.test",
                                        Type = "image",
                                        Text = null
                                    }
                                ]
                            }
                        }
                    ]
                }
            ]
        };

        await _sut.ProcessAsync(payload, _testBusiness);

        _aiParserMock.Verify(
            x => x.ParseAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void ResetAfterConfirm_ResetsMenuSent()
    {
        var state = new ConversationFields { MenuSent = true, CheckoutFormSent = true };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1 });

        state.ResetAfterConfirm();

        state.MenuSent.Should().BeFalse();
        state.CheckoutFormSent.Should().BeFalse();
        state.Items.Should().BeEmpty();
    }

    [Theory]
    [InlineData("hola", true)]
    [InlineData("buenas", true)]
    [InlineData("que tal como estas", true)]
    [InlineData("qué tal cómo estás", true)]
    [InlineData("hey", true)]
    [InlineData("epa", true)]
    [InlineData("saludos", true)]
    [InlineData("buenos dias", true)]
    [InlineData("buenas tardes", true)]
    [InlineData("hola buenas tardes", true)]
    [InlineData("confirmar", false)]
    [InlineData("quiero 2 hamburguesas", false)]
    public async Task IsGreeting_DetectsCorrectly(string input, bool expected)
    {
        var stateStore = new Mock<IConversationStateStore>();
        stateStore
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationFields());
        stateStore
            .Setup(x => x.IsMessageProcessedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        stateStore
            .Setup(x => x.MarkMessageProcessedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        stateStore
            .Setup(x => x.SaveAsync(It.IsAny<string>(), It.IsAny<ConversationFields>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var whatsAppClient = new Mock<IWhatsAppClient>();
        whatsAppClient
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var aiParser = new Mock<IAiParser>();
        aiParser
            .Setup(x => x.ParseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiParseResult
            {
                Intent = RestaurantIntent.General,
                Confidence = 0.5,
                MissingFields = [],
                Args = new ParsedArgs { General = new GeneralArgs { Topic = "test" } }
            });

        var sut = new WebhookProcessor(
            aiParser.Object, whatsAppClient.Object, new Mock<IOrderRepository>().Object,
            stateStore.Object, new Mock<ILogger<WebhookProcessor>>().Object);

        var payload = CreateTextMessagePayload("5511999999999", input);
        await sut.ProcessAsync(payload, new BusinessContext(Guid.NewGuid(), "123456789", "test-token"));

        if (expected)
        {
            whatsAppClient.Verify(
                x => x.SendTextMessageAsync(
                    It.Is<OutgoingMessage>(m => m.Body.Contains("MENU")),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }

    [Fact]
    public async Task ProcessAsync_OrderingIntent_ShowsMenuWithoutAi()
    {
        // "quisiera hacer un pedido" should trigger the ordering-intent path
        // and show the menu + "Que deseas ordenar?" WITHOUT calling AI
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var payload = CreateTextMessagePayload("5511999999999", "quisiera hacer un pedido");

        await _sut.ProcessAsync(payload, _testBusiness);

        // Should NOT call AI parser
        _aiParserMock.Verify(
            x => x.ParseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Should send menu + help
        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(
                It.Is<OutgoingMessage>(m => m.Body.Contains("MENU")),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(
                It.Is<OutgoingMessage>(m => m.Body.Contains("Que deseas ordenar")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_AfterConfirm_GreetingShowsMenuAgain()
    {
        // Simulate a state where MenuSent was true but ResetAfterConfirm was called
        var resetState = new ConversationFields();
        resetState.MenuSent = true;
        resetState.ResetAfterConfirm();
        resetState.MenuSent.Should().BeFalse("ResetAfterConfirm should reset MenuSent");

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resetState);

        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var payload = CreateTextMessagePayload("5511999999999", "hola");

        await _sut.ProcessAsync(payload, _testBusiness);

        // Should send menu since MenuSent was reset
        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(
                It.Is<OutgoingMessage>(m => m.Body.Contains("MENU")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_SendFailure_LogsError()
    {
        // When WhatsAppClient returns false, the logger should receive an error
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var loggerMock = new Mock<ILogger<WebhookProcessor>>();
        var sut = new WebhookProcessor(
            _aiParserMock.Object, _whatsAppClientMock.Object, _orderRepositoryMock.Object,
            _stateStoreMock.Object, loggerMock.Object);

        var payload = CreateTextMessagePayload("5511999999999", "hola");
        await sut.ProcessAsync(payload, _testBusiness);

        // Verify that logger was called with Error level (SEND FAILED)
        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("SEND FAILED")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    private static WebhookPayload CreateTextMessagePayload(string from, string body) => new()
    {
        Object = "whatsapp_business_account",
        Entry =
        [
            new WebhookEntry
            {
                Id = "entry1",
                Changes =
                [
                    new WebhookChange
                    {
                        Field = "messages",
                        Value = new WebhookChangeValue
                        {
                            MessagingProduct = "whatsapp",
                            Metadata = new WebhookMetadata
                            {
                                DisplayPhoneNumber = "15551234567",
                                PhoneNumberId = "123456789"
                            },
                            Contacts =
                            [
                                new WebhookContact
                                {
                                    WaId = from,
                                    Profile = new WebhookProfile { Name = "Test User" }
                                }
                            ],
                            Messages =
                            [
                                new WebhookMessage
                                {
                                    From = from,
                                    Id = "wamid.test123",
                                    Timestamp = "1234567890",
                                    Type = "text",
                                    Text = new WebhookText { Body = body }
                                }
                            ]
                        }
                    }
                ]
            }
        ]
    };
}
