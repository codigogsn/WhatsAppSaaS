using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WhatsAppSaaS.Application.DTOs;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Application.Services;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Api.Tests.Services;

public class WebhookProcessorTests
{
    private readonly Mock<IAiParser> _aiParserMock;
    private readonly Mock<IWhatsAppClient> _whatsAppClientMock;
    private readonly WebhookProcessor _sut;

    public WebhookProcessorTests()
    {
        _aiParserMock = new Mock<IAiParser>();
        _whatsAppClientMock = new Mock<IWhatsAppClient>();

        _sut = new WebhookProcessor(
            _aiParserMock.Object,
            _whatsAppClientMock.Object);
}
    [Fact]
    public async Task ProcessAsync_WithTextMessage_CallsAiParserAndSendsReply()
    {
        // Arrange: AI parser returns an order_create intent with all fields present
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
                        Items = [new OrderItem { Name = "Hamburguesa", Quantity = 2 }],
                        DeliveryType = "pickup"
                    }
                }
            });

        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var payload = CreateTextMessagePayload("5511999999999", "quiero 2 hamburguesas para recoger");

        await _sut.ProcessAsync(payload);

        _aiParserMock.Verify(
            x => x.ParseAsync(
                "quiero 2 hamburguesas para recoger",
                "5511999999999",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(
                It.Is<OutgoingMessage>(m =>
                    m.To == "5511999999999" &&
                    m.Body.Contains("Hamburguesa")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_OrderWithMissingFields_AsksForMissingInfo()
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
                Confidence = 0.85,
                MissingFields = ["delivery_type"],
                Args = new ParsedArgs
                {
                    Order = new OrderArgs
                    {
                        Items = [new OrderItem { Name = "Pizza", Quantity = 1 }]
                    }
                }
            });

        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var payload = CreateTextMessagePayload("5511999999999", "quiero una pizza");

        await _sut.ProcessAsync(payload);

        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(
                It.Is<OutgoingMessage>(m =>
                    m.Body.Contains("recoger") && m.Body.Contains("domicilio")),
                It.IsAny<CancellationToken>()),
            Times.Once);
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

        await _sut.ProcessAsync(payload);

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

        await _sut.ProcessAsync(payload);

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

        await _sut.ProcessAsync(payload);

        _aiParserMock.Verify(
            x => x.ParseAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
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
