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

        _testBusiness = new BusinessContext(Guid.NewGuid(), "123456789", "test-token", "Mi Restaurante");

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
    [InlineData("qu\u00e9 tal c\u00f3mo est\u00e1s", true)]
    [InlineData("hey", true)]
    [InlineData("epa", true)]
    [InlineData("saludos", true)]
    [InlineData("buenos dias", true)]
    [InlineData("buenas tardes", true)]
    [InlineData("hola buenas tardes", true)]
    [InlineData("confirmar", false)]
    [InlineData("quiero 2 hamburguesas", false)]
    public void IsGreeting_DetectsCorrectly(string input, bool expected)
    {
        var t = input.Trim().ToLowerInvariant();
        WebhookProcessor.IsGreeting(t).Should().Be(expected);
    }

    // ── NEW: Greeting sends 3 messages in correct order ──

    [Fact]
    public async Task Greeting_Sends3Messages_WelcomeMenuPrompt()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var payload = CreateTextMessagePayload("5511999999999", "hola");

        await _sut.ProcessAsync(payload, _testBusiness);

        sentMessages.Should().HaveCount(3);

        // Message 1: Welcome with business name
        sentMessages[0].Body.Should().Contain("bienvenido");
        sentMessages[0].Body.Should().Contain("Mi Restaurante");

        // Message 2: Menu
        sentMessages[1].Body.Should().Contain("MEN\u00da");
        sentMessages[1].Body.Should().Contain("Hamburguesa");
        sentMessages[1].Body.Should().Contain("Coca Cola");
        sentMessages[1].Body.Should().Contain("Papas");

        // Message 3: Prompt
        sentMessages[2].Body.Should().Contain("Qu\u00e9 deseas ordenar");
    }

    [Fact]
    public async Task Greeting_WelcomeMessage_IncludesBusinessName()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var biz = new BusinessContext(Guid.NewGuid(), "123456789", "test-token", "Burger Palace");
        var payload = CreateTextMessagePayload("5511999999999", "buenas tardes");

        await _sut.ProcessAsync(payload, biz);

        sentMessages.Should().HaveCountGreaterOrEqualTo(1);
        sentMessages[0].Body.Should().Contain("Burger Palace");
    }

    // ── NEW: Checkout form premium style ──

    [Fact]
    public void CheckoutForm_HasPremiumStyle()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // Premium emoji labels
        reply.Should().Contain("Nombre:");
        reply.Should().Contain("C\u00e9dula:");
        reply.Should().Contain("Tel\u00e9fono:");
        reply.Should().Contain("Direcci\u00f3n:");
        reply.Should().Contain("Pago:");
        reply.Should().Contain("EFECTIVO / DIVISAS / PAGO M\u00d3VIL");
        reply.Should().Contain("Ubicaci\u00f3n GPS:");
        reply.Should().Contain("OBLIGATORIO");
        reply.Should().Contain("CONFIRMAR");
    }

    // ── NEW: Pago Móvil sends payment details + proof request ──

    [Fact]
    public async Task PagoMovil_SendsPaymentDetails_ThenProofRequest()
    {
        // Set up env vars for payment config
        Environment.SetEnvironmentVariable("PAYMENT_MOBILE_BANK", "Banesco");
        Environment.SetEnvironmentVariable("PAYMENT_MOBILE_ID", "V-12345678");
        Environment.SetEnvironmentVariable("PAYMENT_MOBILE_PHONE", "0412-1234567");

        try
        {
            var sentMessages = new List<OutgoingMessage>();
            _whatsAppClientMock
                .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
                .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
                .ReturnsAsync(true);

            // Prepare state: items + delivery + checkout form sent
            var state = new ConversationFields();
            state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 2 });
            state.DeliveryType = "delivery";
            state.CheckoutFormSent = true;

            _stateStoreMock
                .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(state);

            var formText = "Nombre: Juan\nC\u00e9dula: V-12345\nTel\u00e9fono: 0412-1234567\nDirecci\u00f3n: Calle 1\nPago: pago m\u00f3vil";
            var payload = CreateTextMessagePayload("5511999999999", formText);

            await _sut.ProcessAsync(payload, _testBusiness);

            // Should have: payment details + proof request + "Perfecto" confirmation
            var pagoDetailMsg = sentMessages.FirstOrDefault(m => m.Body.Contains("DATOS PARA PAGO"));
            pagoDetailMsg.Should().NotBeNull("should send payment details");
            pagoDetailMsg!.Body.Should().Contain("Banesco");
            pagoDetailMsg.Body.Should().Contain("V-12345678");
            pagoDetailMsg.Body.Should().Contain("0412-1234567");

            var proofMsg = sentMessages.FirstOrDefault(m => m.Body.Contains("comprobante"));
            proofMsg.Should().NotBeNull("should request proof photo");

            // Payment details should come before proof request
            var detailIdx = sentMessages.IndexOf(pagoDetailMsg);
            var proofIdx = sentMessages.IndexOf(proofMsg!);
            detailIdx.Should().BeLessThan(proofIdx, "payment details should come before proof request");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PAYMENT_MOBILE_BANK", null);
            Environment.SetEnvironmentVariable("PAYMENT_MOBILE_ID", null);
            Environment.SetEnvironmentVariable("PAYMENT_MOBILE_PHONE", null);
        }
    }

    // ── NEW: Confirmed receipt uses premium format ──

    [Fact]
    public async Task ConfirmedReceipt_UsesPremiumFormat()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields
        {
            CheckoutFormSent = true,
            CustomerName = "Juan P\u00e9rez",
            CustomerIdNumber = "V-12345678",
            CustomerPhone = "0412-1234567",
            Address = "Calle Principal #10",
            PaymentMethod = "efectivo",
            GpsPinReceived = true,
            DeliveryType = "delivery"
        };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 2 });
        state.Items.Add(new ConversationItemEntry { Name = "Coca Cola", Quantity = 1 });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var payload = CreateTextMessagePayload("5511999999999", "confirmar");

        await _sut.ProcessAsync(payload, _testBusiness);

        var receipt = sentMessages.FirstOrDefault(m => m.Body.Contains("PEDIDO CONFIRMADO"));
        receipt.Should().NotBeNull("should send confirmed receipt");

        var body = receipt!.Body;
        body.Should().Contain("PEDIDO CONFIRMADO");
        body.Should().Contain("Pedido: #");
        body.Should().Contain("Nombre: Juan P\u00e9rez");
        body.Should().Contain("C\u00e9dula: V-12345678");
        body.Should().Contain("Pedido: 2 Hamburguesa, 1 Coca Cola");
        body.Should().Contain("Direcci\u00f3n: Calle Principal #10");
        body.Should().Contain("Pago: EFECTIVO");
        body.Should().Contain("Gracias");
    }

    [Fact]
    public async Task ProcessAsync_OrderingIntent_ShowsMenuWithoutAi()
    {
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var payload = CreateTextMessagePayload("5511999999999", "quisiera hacer un pedido");

        await _sut.ProcessAsync(payload, _testBusiness);

        // Should NOT call AI parser
        _aiParserMock.Verify(
            x => x.ParseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Should send greeting sequence (3 messages including menu)
        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(
                It.Is<OutgoingMessage>(m => m.Body.Contains("MEN\u00da")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_AfterConfirm_GreetingShowsMenuAgain()
    {
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
                It.Is<OutgoingMessage>(m => m.Body.Contains("MEN\u00da")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessAsync_SendFailure_LogsError()
    {
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var loggerMock = new Mock<ILogger<WebhookProcessor>>();
        var sut = new WebhookProcessor(
            _aiParserMock.Object, _whatsAppClientMock.Object, _orderRepositoryMock.Object,
            _stateStoreMock.Object, loggerMock.Object);

        var payload = CreateTextMessagePayload("5511999999999", "hola");
        await sut.ProcessAsync(payload, _testBusiness);

        loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("SEND FAILED")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public void TryParseCheckoutForm_PagoMovil_ParsesCorrectly()
    {
        var formText = "Nombre: Juan\nC\u00e9dula: V-12345\nTel\u00e9fono: 0412-1234567\nDirecci\u00f3n: Calle 1\nPago: pago m\u00f3vil";
        var result = WebhookProcessor.TryParseCheckoutForm(formText, out var form);
        result.Should().BeTrue("form has 5 filled fields");
        form.PaymentMethod.Should().Be("pago_movil");
        form.CustomerName.Should().Be("Juan");
    }

    // ── STALE STATE RESET TESTS ──

    private ConversationFields CreateStaleCheckoutState()
    {
        var state = new ConversationFields
        {
            MenuSent = true,
            CheckoutFormSent = true,
            CustomerName = "Old Customer",
            CustomerIdNumber = "V-OLD",
            CustomerPhone = "0412-0000000",
            Address = "Old Address",
            PaymentMethod = "efectivo",
            GpsPinReceived = true,
            DeliveryType = "delivery"
        };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1 });
        return state;
    }

    [Fact]
    public async Task StaleCheckout_HolaQueTal_ResetsAndSends3FreshMessages()
    {
        var staleState = CreateStaleCheckoutState();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(staleState);

        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var payload = CreateTextMessagePayload("5511999999999", "hola que tal");
        await _sut.ProcessAsync(payload, _testBusiness);

        sentMessages.Should().HaveCount(3, "should send welcome + menu + prompt");
        sentMessages[0].Body.Should().Contain("bienvenido");
        sentMessages[1].Body.Should().Contain("MEN\u00da");
        sentMessages[2].Body.Should().Contain("Qu\u00e9 deseas ordenar");

        // State must be reset
        staleState.Items.Should().BeEmpty("stale items should be cleared");
        staleState.CheckoutFormSent.Should().BeFalse("checkout form flag should be reset");
        staleState.CustomerName.Should().BeNull("customer data should be cleared");
    }

    [Fact]
    public async Task StaleCheckout_QuieroHacerUnPedido_ResetsAndStartsFresh()
    {
        var staleState = CreateStaleCheckoutState();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(staleState);

        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var payload = CreateTextMessagePayload("5511999999999", "quiero hacer un pedido");
        await _sut.ProcessAsync(payload, _testBusiness);

        sentMessages.Should().HaveCount(3, "should send welcome + menu + prompt");
        sentMessages[0].Body.Should().Contain("bienvenido");
        sentMessages[1].Body.Should().Contain("MEN\u00da");

        // Must NOT say "Sigue llenando la planilla"
        sentMessages.Should().NotContain(m => m.Body.Contains("planilla"));
    }

    [Fact]
    public async Task StaleCheckout_MeMandasElMenu_ResetsAndSendsMenu()
    {
        var staleState = CreateStaleCheckoutState();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(staleState);

        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var payload = CreateTextMessagePayload("5511999999999", "me mandas el menu?");
        await _sut.ProcessAsync(payload, _testBusiness);

        sentMessages.Should().HaveCount(3, "should send welcome + menu + prompt");
        sentMessages[1].Body.Should().Contain("MEN\u00da");
        sentMessages[1].Body.Should().Contain("Hamburguesa");
    }

    [Theory]
    [InlineData("hola")]
    [InlineData("hola que tal")]
    [InlineData("buenas tardes")]
    [InlineData("menu")]
    [InlineData("men\u00fa")]
    [InlineData("quiero ver el menu")]
    [InlineData("mandame el menu")]
    [InlineData("quiero hacer un pedido")]
    [InlineData("quisiera hacer un pedido")]
    [InlineData("nuevo pedido")]
    [InlineData("empezar de nuevo")]
    public async Task RestartIntent_AlwaysWinsOverStaleCheckout(string input)
    {
        var staleState = CreateStaleCheckoutState();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(staleState);

        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var payload = CreateTextMessagePayload("5511999999999", input);
        await _sut.ProcessAsync(payload, _testBusiness);

        sentMessages.Should().HaveCount(3, $"'{input}' should reset and send 3 fresh messages");
        sentMessages[0].Body.Should().Contain("bienvenido", $"'{input}' should trigger welcome");
        sentMessages[1].Body.Should().Contain("MEN\u00da", $"'{input}' should trigger menu");
        sentMessages[2].Body.Should().Contain("Qu\u00e9 deseas ordenar", $"'{input}' should trigger prompt");

        // Must NOT contain stale checkout messages
        sentMessages.Should().NotContain(m => m.Body.Contains("planilla"), $"'{input}' should not trigger stale checkout");
        sentMessages.Should().NotContain(m => m.Body.Contains("falta informaci\u00f3n"), $"'{input}' should not trigger missing-info");
    }

    // ── IsRestartIntent unit tests ──

    [Theory]
    [InlineData("menu", true)]
    [InlineData("men\u00fa", true)]
    [InlineData("me mandas el menu", true)]
    [InlineData("quiero ver el menu", true)]
    [InlineData("mandame el menu", true)]
    [InlineData("hola", true)]
    [InlineData("quiero hacer un pedido", true)]
    [InlineData("nuevo pedido", true)]
    [InlineData("empezar de nuevo", true)]
    [InlineData("reiniciar pedido", true)]
    [InlineData("deseo pedir", true)]
    [InlineData("confirmar", false)]
    [InlineData("agrega 2 hamburguesas", false)]
    [InlineData("Nombre: Juan", false)]
    public void IsRestartIntent_DetectsCorrectly(string input, bool expected)
    {
        var t = input.Trim().ToLowerInvariant();
        WebhookProcessor.IsRestartIntent(t).Should().Be(expected);
    }

    // ── Parser cleanup tests ──

    [Theory]
    [InlineData("pago movil pago movil", "pago_movil")]
    [InlineData("PAGO MOVIL", "pago_movil")]
    [InlineData("pago m\u00f3vil", "pago_movil")]
    [InlineData("efectivo", "efectivo")]
    [InlineData("EFECTIVO", "efectivo")]
    [InlineData("divisas", "divisas")]
    public void CheckoutParser_PaymentNormalization_NoDuplicates(string payValue, string expected)
    {
        var formText = $"Nombre: Juan\nC\u00e9dula: V-12345\nTel\u00e9fono: 0412-1234567\nPago: {payValue}";
        var result = WebhookProcessor.TryParseCheckoutForm(formText, out var form);
        result.Should().BeTrue();
        form.PaymentMethod.Should().Be(expected);
    }

    [Fact]
    public void DeduplicateTokens_RemovesDuplicatedHalves()
    {
        WebhookProcessor.DeduplicateTokens("pago movil pago movil").Should().Be("pago movil");
        WebhookProcessor.DeduplicateTokens("efectivo").Should().Be("efectivo");
        WebhookProcessor.DeduplicateTokens("hello world").Should().Be("hello world");
    }

    [Fact]
    public void CleanFieldValue_TrimsAndCollapsesSpaces()
    {
        WebhookProcessor.CleanFieldValue("  Juan  P\u00e9rez  ").Should().Be("Juan P\u00e9rez");
        WebhookProcessor.CleanFieldValue("*bold text*").Should().Be("bold text");
        WebhookProcessor.CleanFieldValue(null).Should().BeNull();
        WebhookProcessor.CleanFieldValue("  ").Should().BeNull();
    }

    [Fact]
    public void CheckoutParser_CleansFieldValues()
    {
        var formText = "Nombre:  Juan   P\u00e9rez  \nC\u00e9dula: V-12345\nTel\u00e9fono: 0412-1234567\nDirecci\u00f3n:  Alto  Hatillo \nPago: efectivo";
        var result = WebhookProcessor.TryParseCheckoutForm(formText, out var form);
        result.Should().BeTrue();
        form.CustomerName.Should().Be("Juan P\u00e9rez");
        form.Address.Should().Be("Alto Hatillo");
    }

    // ── Emoji correctness tests ──

    [Fact]
    public void CheckoutForm_UsesCedulaIdCardEmoji()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // Must contain 🪪 (U+1FAAA = ID card), NOT 🪭 (U+1FAAD = fan)
        reply.Should().Contain("\ud83e\udeaa", "should use ID card emoji for C\u00e9dula");
        reply.Should().NotContain("\ud83e\udead", "should NOT use fan emoji");
    }

    [Fact]
    public void DeliveryTypePrompt_HasShoppingBagEmoji()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1 });
        // No delivery type set — should prompt

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);
        reply.Should().Contain("pick up");
        reply.Should().Contain("delivery");
    }

    // ── "quiero hacer un nuevo pedido" must also reset (user should NOT need "NUEVO") ──

    [Fact]
    public async Task StaleCheckout_QuieroHacerUnNuevoPedido_AlsoResets()
    {
        var staleState = CreateStaleCheckoutState();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(staleState);

        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        // Both with and without "nuevo" should reset
        var payload = CreateTextMessagePayload("5511999999999", "quiero hacer un nuevo pedido");
        await _sut.ProcessAsync(payload, _testBusiness);

        sentMessages.Should().HaveCount(3);
        sentMessages[0].Body.Should().Contain("bienvenido");
    }

    private static int _msgCounter;

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
                                    Id = $"wamid.test{Interlocked.Increment(ref _msgCounter)}",
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

    // ══════════════════════════════════════════════
    // ORDER MODIFICATION TESTS
    // ══════════════════════════════════════════════

    [Theory]
    [InlineData("agrega 3 hamburgueAs mas porfavor", "Hamburguesa", 3)]
    [InlineData("agrega 2 cocas", "Coca Cola", 2)]
    [InlineData("agregame 1 papa", "Papas", 1)]
    [InlineData("suma 2 hamburguesas", "Hamburguesa", 2)]
    [InlineData("pon 4 papas", "Papas", 4)]
    [InlineData("2 hamburguesas más", "Hamburguesa", 2)]
    [InlineData("agrega 1 hamburgesa mas porfa", "Hamburguesa", 1)]
    public void TryParseOrderModification_Add_ParsesCorrectly(string input, string expectedItem, int expectedQty)
    {
        var result = WebhookProcessor.TryParseOrderModification(input, out var mod);

        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.Add);
        mod.ItemName.Should().Be(expectedItem);
        mod.Quantity.Should().Be(expectedQty);
    }

    [Theory]
    [InlineData("quita 1 papa", "Papas", 1)]
    [InlineData("elimina 2 cocas", "Coca Cola", 2)]
    [InlineData("sin las papas", "Papas", int.MaxValue)]
    [InlineData("borra una hamburguesa", "Hamburguesa", 1)]
    public void TryParseOrderModification_Remove_ParsesCorrectly(string input, string expectedItem, int expectedQty)
    {
        var result = WebhookProcessor.TryParseOrderModification(input, out var mod);

        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.Remove);
        mod.ItemName.Should().Be(expectedItem);
        mod.Quantity.Should().Be(expectedQty);
    }

    [Theory]
    [InlineData("cambia a 3 hamburguesas", "Hamburguesa", 3)]
    [InlineData("mejor 2 cocas", "Coca Cola", 2)]
    public void TryParseOrderModification_Replace_ParsesCorrectly(string input, string expectedItem, int expectedQty)
    {
        var result = WebhookProcessor.TryParseOrderModification(input, out var mod);

        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.Replace);
        mod.ItemName.Should().Be(expectedItem);
        mod.Quantity.Should().Be(expectedQty);
    }

    // ── Typo tolerance ──

    [Theory]
    [InlineData("hamburgueas", "Hamburguesa")]
    [InlineData("hamburgesa", "Hamburguesa")]
    [InlineData("hamburgesas", "Hamburguesa")]
    [InlineData("hamburguesas", "Hamburguesa")]
    [InlineData("cocacolas", "Coca Cola")]
    [InlineData("cocas", "Coca Cola")]
    [InlineData("coca cola", "Coca Cola")]
    [InlineData("papaas", "Papas")]
    [InlineData("papas", "Papas")]
    [InlineData("papa", "Papas")]
    [InlineData("papitas", "Papas")]
    public void NormalizeMenuItemName_ResolvesTypos(string input, string expected)
    {
        var result = WebhookProcessor.NormalizeMenuItemName(input);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("hola")]
    [InlineData("confirmar")]
    [InlineData("xyz")]
    [InlineData("direccion")]
    public void NormalizeMenuItemName_RejectsNonMenuItems(string input)
    {
        var result = WebhookProcessor.NormalizeMenuItemName(input);
        result.Should().BeNull();
    }

    // ── Integration: modification updates structured state, not raw text ──

    [Fact]
    public async Task OrderModification_AddWithTypo_UpdatesStructuredItems()
    {
        // Arrange: existing order with 2 hamburguesas
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 2 });
        state.Items.Add(new ConversationItemEntry { Name = "Coca Cola", Quantity = 3 });
        state.Items.Add(new ConversationItemEntry { Name = "Papas", Quantity = 6 });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var payload = CreateTextMessagePayload("5511999999999","agrega 3 hamburgueAs mas porfavor");

        // Act
        await _sut.ProcessAsync(payload, _testBusiness);

        // Assert: Hamburguesa quantity should be 5 (2 + 3)
        state.Items.Should().HaveCount(3);
        state.Items.First(i => i.Name == "Hamburguesa").Quantity.Should().Be(5);
        state.Items.First(i => i.Name == "Coca Cola").Quantity.Should().Be(3);
        state.Items.First(i => i.Name == "Papas").Quantity.Should().Be(6);

        // Assert: no raw text item was added
        state.Items.Should().NotContain(i => i.Name.Contains("hamburgueAs"));
        state.Items.Should().NotContain(i => i.Name.Contains("porfavor"));
    }

    [Fact]
    public async Task OrderModification_Add_ResponseMentionsCleanItemName()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 2 });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        string? sentBody = null;
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((msg, _) => sentBody = msg.Body)
            .ReturnsAsync(true);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999","agrega 3 hamburgueas mas porfavor"), _testBusiness);

        sentBody.Should().NotBeNull();
        sentBody.Should().Contain("3 Hamburguesa");
        sentBody.Should().Contain("CONFIRMAR");
        sentBody.Should().NotContain("hamburgueAs");
        sentBody.Should().NotContain("porfavor");
    }

    [Fact]
    public async Task OrderModification_Remove_DecreasesQuantity()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Papas", Quantity = 6 });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999","quita 2 papas"), _testBusiness);

        state.Items.Should().HaveCount(1);
        state.Items.First().Quantity.Should().Be(4);
    }

    [Fact]
    public async Task OrderModification_RemoveAll_RemovesItem()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Papas", Quantity = 6 });
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 2 });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999","sin las papas"), _testBusiness);

        state.Items.Should().HaveCount(1);
        state.Items.First().Name.Should().Be("Hamburguesa");
    }

    [Fact]
    public async Task OrderModification_Replace_SetsExactQuantity()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 2 });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999","cambia a 5 hamburguesas"), _testBusiness);

        state.Items.Should().HaveCount(1);
        state.Items.First().Quantity.Should().Be(5);
    }

    [Fact]
    public async Task ReceiptAfterModification_ShowsCleanItems()
    {
        // Setup: order with items, checkout form completed, then modification, then confirm
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 2 });
        state.Items.Add(new ConversationItemEntry { Name = "Coca Cola", Quantity = 3 });
        state.Items.Add(new ConversationItemEntry { Name = "Papas", Quantity = 6 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;
        state.CheckoutFormSent = true;
        state.CustomerName = "Juan";
        state.CustomerIdNumber = "12345678";
        state.CustomerPhone = "04141234567";
        state.Address = "Calle 1";
        state.PaymentMethod = "efectivo";
        state.GpsPinReceived = true;

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var sentBodies = new List<string>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((msg, _) => sentBodies.Add(msg.Body))
            .ReturnsAsync(true);

        // Step 1: Modify the order
        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999","agrega 3 hamburgueas mas porfavor"), _testBusiness);
        state.Items.First(i => i.Name == "Hamburguesa").Quantity.Should().Be(5);

        // Step 2: Confirm
        sentBodies.Clear();
        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999","confirmar"), _testBusiness);

        // The receipt should contain clean item names only
        var receipt = sentBodies.LastOrDefault() ?? "";
        receipt.Should().Contain("5 Hamburguesa");
        receipt.Should().Contain("3 Coca Cola");
        receipt.Should().Contain("6 Papas");
        receipt.Should().NotContain("hamburgueAs");
        receipt.Should().NotContain("porfavor");
        receipt.Should().Contain("PEDIDO CONFIRMADO");
    }

    // ══════════════════════════════════════════════
    // OBSERVATION / SPECIAL INSTRUCTIONS TESTS
    // ══════════════════════════════════════════════

    [Fact]
    public void BuildOrderReply_AfterDeliveryType_ShowsObservationPrompt()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1 });
        state.DeliveryType = "delivery";

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        reply.Should().Contain("observaci\u00f3n especial");
        reply.Should().Contain("NO");
        state.ObservationPromptSent.Should().BeTrue();
    }

    [Fact]
    public void BuildOrderReply_WithExistingObservation_ShowsDetected()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1 });
        state.DeliveryType = "delivery";
        state.SpecialInstructions = "sin cebolla";

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        reply.Should().Contain("sin cebolla");
        reply.Should().Contain("Observaci\u00f3n detectada");
    }

    [Fact]
    public async Task ObservationAnswer_No_ContinuesWithoutObservation()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        string? sentBody = null;
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((msg, _) => sentBody = msg.Body)
            .ReturnsAsync(true);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "no"), _testBusiness);

        state.ObservationAnswered.Should().BeTrue();
        state.SpecialInstructions.Should().BeNull();
        // Should show checkout form
        sentBody.Should().Contain("Nombre");
        sentBody.Should().Contain("CONFIRMAR");
    }

    [Fact]
    public async Task ObservationAnswer_FreeText_StoresObservation()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 2 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "1 sin cebolla, 1 con extra queso"), _testBusiness);

        state.ObservationAnswered.Should().BeTrue();
        state.SpecialInstructions.Should().Be("1 sin cebolla, 1 con extra queso");
    }

    [Fact]
    public async Task ObservationAnswer_AppendsToExisting()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 2 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.SpecialInstructions = "sin cebolla";

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "extra queso"), _testBusiness);

        state.SpecialInstructions.Should().Be("sin cebolla; extra queso");
    }

    // ── Embedded observation detection ──

    [Theory]
    [InlineData("1 hamburguesa sin cebolla", "sin cebolla")]
    [InlineData("2 hamburguesas con extra queso", "con extra queso")]
    [InlineData("1 coca cola sin hielo", "sin hielo")]
    public void ExtractEmbeddedObservation_DetectsModifiers(string input, string expected)
    {
        var result = WebhookProcessor.ExtractEmbeddedObservation(input);
        result.Should().NotBeNull();
        result.Should().Contain(expected);
    }

    [Fact]
    public void ExtractEmbeddedObservation_NoModifier_ReturnsNull()
    {
        var result = WebhookProcessor.ExtractEmbeddedObservation("2 hamburguesas delivery");
        result.Should().BeNull();
    }

    [Fact]
    public async Task QuickOrder_WithEmbeddedObservation_StoresIt()
    {
        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "1 hamburguesa sin cebolla delivery"), _testBusiness);

        state.Items.Should().HaveCount(1);
        state.Items.First().Name.Should().Be("Hamburguesa");
        state.SpecialInstructions.Should().Contain("sin cebolla");
    }

    // ── Final receipt includes observation ──

    [Fact]
    public async Task FinalReceipt_IncludesObservationWhenPresent()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 2 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;
        state.SpecialInstructions = "1 sin cebolla, 1 con extra queso";
        state.CheckoutFormSent = true;
        state.CustomerName = "Ana";
        state.CustomerIdNumber = "12345678";
        state.CustomerPhone = "04141234567";
        state.Address = "Calle 2";
        state.PaymentMethod = "efectivo";
        state.GpsPinReceived = true;

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var sentBodies = new List<string>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((msg, _) => sentBodies.Add(msg.Body))
            .ReturnsAsync(true);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "confirmar"), _testBusiness);

        var receipt = sentBodies.Last();
        receipt.Should().Contain("PEDIDO CONFIRMADO");
        receipt.Should().Contain("Observaci\u00f3n: 1 sin cebolla, 1 con extra queso");
    }

    [Fact]
    public async Task FinalReceipt_NoObservationLine_WhenEmpty()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;
        state.CheckoutFormSent = true;
        state.CustomerName = "Ana";
        state.CustomerIdNumber = "12345678";
        state.CustomerPhone = "04141234567";
        state.Address = "Calle 2";
        state.PaymentMethod = "efectivo";
        state.GpsPinReceived = true;

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var sentBodies = new List<string>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((msg, _) => sentBodies.Add(msg.Body))
            .ReturnsAsync(true);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "confirmar"), _testBusiness);

        var receipt = sentBodies.Last();
        receipt.Should().Contain("PEDIDO CONFIRMADO");
        receipt.Should().NotContain("Observaci\u00f3n");
    }

    // ── Human handoff ──

    [Theory]
    [InlineData("humano")]
    [InlineData("agente")]
    [InlineData("asesor")]
    [InlineData("persona")]
    [InlineData("soporte")]
    public void IsHumanHandoffRequest_DetectsCorrectly(string input)
    {
        WebhookProcessor.IsHumanHandoffRequest(input).Should().BeTrue();
    }

    [Theory]
    [InlineData("hola")]
    [InlineData("confirmar")]
    [InlineData("2 hamburguesas")]
    public void IsHumanHandoffRequest_RejectsNonHandoff(string input)
    {
        WebhookProcessor.IsHumanHandoffRequest(input).Should().BeFalse();
    }

    [Fact]
    public async Task HumanHandoff_PreservesOrderContext()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 2 });
        state.DeliveryType = "delivery";

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        string? sentBody = null;
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((msg, _) => sentBody = msg.Body)
            .ReturnsAsync(true);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "humano"), _testBusiness);

        state.HumanHandoffRequested.Should().BeTrue();
        state.Items.Should().HaveCount(1); // order preserved
        state.Items.First().Quantity.Should().Be(2);
        sentBody.Should().Contain("humano");
    }

    // ── No observation variants ──

    [Theory]
    [InlineData("no")]
    [InlineData("NO")]
    [InlineData("no tengo")]
    [InlineData("ninguna")]
    [InlineData("sin observaciones")]
    [InlineData("nada")]
    public void IsNoObservation_DetectsCorrectly(string input)
    {
        WebhookProcessor.IsNoObservation(input.Trim().ToLowerInvariant()).Should().BeTrue();
    }

    // ── Item modification is NOT confused with observation ──

    [Fact]
    public async Task ItemModification_NotConfusedWithObservation()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 2 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;
        state.CheckoutFormSent = true;

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // This should be parsed as an item modification, not an observation
        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "agrega 1 coca"), _testBusiness);

        state.Items.Should().HaveCount(2);
        state.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    // ── Restart still works after observation addition ──

    [Fact]
    public async Task RestartIntent_ClearsObservationState()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1 });
        state.SpecialInstructions = "sin cebolla";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "hola"), _testBusiness);

        state.SpecialInstructions.Should().BeNull();
        state.ObservationPromptSent.Should().BeFalse();
        state.ObservationAnswered.Should().BeFalse();
        state.Items.Should().BeEmpty();
    }

    // ══════════════════════════════════════════════════
    // Checkout Form Parsing Tests
    // ══════════════════════════════════════════════════

    [Fact]
    public void TryParseCheckoutForm_LabeledFormat_ParsesCorrectly()
    {
        var text = "Nombre: Adal\nC\u00e9dula: 27302670\nTel\u00e9fono: 04242309349\nDirecci\u00f3n: Alto Hatillo\nPago: pago m\u00f3vil";

        WebhookProcessor.TryParseCheckoutForm(text, out var form).Should().BeTrue();

        form.CustomerName.Should().Be("Adal");
        form.CustomerIdNumber.Should().Be("27302670");
        form.CustomerPhone.Should().Be("04242309349");
        form.Address.Should().Be("Alto Hatillo");
        form.PaymentMethod.Should().Be("pago_movil");
    }

    [Fact]
    public void TryParseCheckoutForm_PlainMultiline_ParsesCorrectly()
    {
        var text = "adal\n27302670\n04242309349\nalto hatillo\npago movil";

        WebhookProcessor.TryParseCheckoutForm(text, out var form).Should().BeTrue();

        form.CustomerName.Should().Be("adal");
        form.CustomerIdNumber.Should().Be("27302670");
        form.CustomerPhone.Should().Be("04242309349");
        form.Address.Should().Be("alto hatillo");
        form.PaymentMethod.Should().Be("pago_movil");
    }

    [Fact]
    public void TryParseCheckoutForm_HybridFormat_ParsesCorrectly()
    {
        var text = "Adal\n27302670\n04242309349\nDirecci\u00f3n: Alto Hatillo\nPago m\u00f3vil";

        WebhookProcessor.TryParseCheckoutForm(text, out var form).Should().BeTrue();

        form.CustomerName.Should().Be("Adal");
        form.CustomerIdNumber.Should().Be("27302670");
        form.CustomerPhone.Should().Be("04242309349");
        form.Address.Should().Be("Alto Hatillo");
        form.PaymentMethod.Should().Be("pago_movil");
    }

    [Fact]
    public void TryParseCheckoutForm_PhoneOnlyLine_Recognized()
    {
        var text = "Maria\n04141234567";

        WebhookProcessor.TryParseCheckoutForm(text, out var form).Should().BeTrue();

        form.CustomerName.Should().Be("Maria");
        form.CustomerPhone.Should().Be("04141234567");
    }

    [Fact]
    public void TryParseCheckoutForm_CedulaOnlyLine_Recognized()
    {
        var text = "Jose\n18456789";

        WebhookProcessor.TryParseCheckoutForm(text, out var form).Should().BeTrue();

        form.CustomerName.Should().Be("Jose");
        form.CustomerIdNumber.Should().Be("18456789");
    }

    [Fact]
    public void TryParseCheckoutForm_PaymentOnlyLine_Recognized()
    {
        var text = "Maria\nefectivo";

        WebhookProcessor.TryParseCheckoutForm(text, out var form).Should().BeTrue();

        form.CustomerName.Should().Be("Maria");
        form.PaymentMethod.Should().Be("efectivo");
    }

    [Theory]
    [InlineData("pago movil", "pago_movil")]
    [InlineData("pago m\u00f3vil", "pago_movil")]
    [InlineData("pm", "pago_movil")]
    [InlineData("efectivo", "efectivo")]
    [InlineData("cash", "efectivo")]
    [InlineData("divisas", "divisas")]
    public void NormalizePaymentMethod_VariousInputs(string input, string expected)
    {
        WebhookProcessor.NormalizePaymentMethod(input).Should().Be(expected);
    }

    [Fact]
    public async Task CheckoutForm_PlainMultiline_MergesIntoState()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 2 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;
        state.CheckoutFormSent = true;

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "adal\n27302670\n04242309349\nalto hatillo\npago movil"),
            _testBusiness);

        state.CustomerName.Should().Be("adal");
        state.CustomerIdNumber.Should().Be("27302670");
        state.CustomerPhone.Should().Be("04242309349");
        state.Address.Should().Be("alto hatillo");
        state.PaymentMethod.Should().Be("pago_movil");

        sentMessages.Should().Contain(m => m.Body.Contains("CONFIRMAR"));
    }

    [Fact]
    public async Task CheckoutForm_PartialSubmission_MergesAndKeepsPrevious()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;
        state.CheckoutFormSent = true;
        state.CustomerName = "Carlos";  // Already have name

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // Send only phone and cedula
        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "18456789\n04141234567"),
            _testBusiness);

        state.CustomerName.Should().Be("Carlos"); // Kept from before
        state.CustomerIdNumber.Should().Be("18456789");
        state.CustomerPhone.Should().Be("04141234567");
    }

    [Fact]
    public async Task ConfirmBeforeFullData_ShowsCanonicalMissingFields()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;
        state.CheckoutFormSent = true;
        // Only name filled, everything else missing

        state.CustomerName = "Juan";

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "confirmar"), _testBusiness);

        var reply = sentMessages.Last().Body;
        reply.Should().Contain("A\u00fan falta informaci\u00f3n para confirmar.");
        reply.Should().Contain("\u2022 \ud83e\udeaa C\u00e9dula:");
        reply.Should().Contain("\u2022 \ud83d\udcf1 Tel\u00e9fono:");
        reply.Should().Contain("\u2022 \ud83c\udfe1 Direcci\u00f3n:");
        reply.Should().Contain("\u2022 \ud83d\udcb5 Pago:");
        reply.Should().NotContain("\u2022 \ud83d\udc64 Nombre:"); // Name already filled
        reply.Should().Contain("Luego escribe *CONFIRMAR*.");
        reply.Should().Contain("l\u00edneas separadas");
    }

    [Fact]
    public async Task ConfirmBeforeFullData_DoesNotCorruptExistingState()
    {
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;
        state.CheckoutFormSent = true;
        state.CustomerName = "Juan";
        state.CustomerPhone = "04141234567";

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "confirmar"), _testBusiness);

        // State should not be corrupted
        state.CustomerName.Should().Be("Juan");
        state.CustomerPhone.Should().Be("04141234567");
        state.Items.Should().HaveCount(1);
    }

    [Fact]
    public void IsVenezuelanPhone_VariousFormats()
    {
        WebhookProcessor.IsVenezuelanPhone("04242309349").Should().BeTrue();
        WebhookProcessor.IsVenezuelanPhone("04141234567").Should().BeTrue();
        WebhookProcessor.IsVenezuelanPhone("04121234567").Should().BeTrue();
        WebhookProcessor.IsVenezuelanPhone("+584242309349").Should().BeTrue();
        WebhookProcessor.IsVenezuelanPhone("584242309349").Should().BeTrue();
        WebhookProcessor.IsVenezuelanPhone("12345").Should().BeFalse();
        WebhookProcessor.IsVenezuelanPhone("27302670").Should().BeFalse();
    }

    [Fact]
    public void BuildCanonicalMissingFieldsMessage_FormatIsStable()
    {
        var missing = new List<string>
        {
            "\u2022 \ud83c\udfe1 Direcci\u00f3n:",
        };

        var result = WebhookProcessor.BuildCanonicalMissingFieldsMessage(missing);
        result.Should().Contain("A\u00fan falta informaci\u00f3n para confirmar.");
        result.Should().Contain("\u2022 \ud83c\udfe1 Direcci\u00f3n:");
        result.Should().Contain("l\u00edneas separadas");
        result.Should().Contain("Luego escribe *CONFIRMAR*.");
    }

    [Fact]
    public async Task CheckoutPrompt_IncludesMultiFormatInstruction()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;
        // CheckoutFormSent = false => should trigger checkout prompt

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // Trigger via quick order that reaches BuildOrderReplyFromState
        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "1 hamburguesa delivery"), _testBusiness);

        var checkoutPrompt = sentMessages.FirstOrDefault(m => m.Body.Contains("Nombre:"));
        checkoutPrompt.Should().NotBeNull();
        checkoutPrompt!.Body.Should().Contain("l\u00edneas separadas");
        checkoutPrompt.Body.Should().Contain("planilla");
    }

    [Fact]
    public async Task FullFlow_PlainCheckout_GpsAndConfirm()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        // State: ready for checkout form
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 2 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;
        state.CheckoutFormSent = true;

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // Step 1: Send plain multiline checkout
        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "Juan\n27302670\n04242309349\nalto hatillo\nefectivo"),
            _testBusiness);

        state.CustomerName.Should().Be("Juan");
        state.PaymentMethod.Should().Be("efectivo");

        // Step 2: GPS pin
        state.GpsPinReceived = true;

        // Step 3: Confirm
        sentMessages.Clear();
        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "confirmar"), _testBusiness);

        var receipt = sentMessages.FirstOrDefault(m => m.Body.Contains("PEDIDO CONFIRMADO"));
        receipt.Should().NotBeNull();
        receipt!.Body.Should().Contain("Nombre: Juan");
        receipt.Body.Should().Contain("2 Hamburguesa");
    }

    [Fact]
    public async Task SpecialInstructions_StillWorksWithPlainCheckout()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;
        state.SpecialInstructions = "sin cebolla";
        state.CheckoutFormSent = true;

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // Plain multiline checkout
        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "Maria\n18456789\n04141234567\nla castellana\nefectivo"),
            _testBusiness);

        state.CustomerName.Should().Be("Maria");
        state.SpecialInstructions.Should().Be("sin cebolla"); // Preserved

        // GPS + confirm
        state.GpsPinReceived = true;
        sentMessages.Clear();
        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "confirmar"), _testBusiness);

        var receipt = sentMessages.FirstOrDefault(m => m.Body.Contains("PEDIDO CONFIRMADO"));
        receipt.Should().NotBeNull();
        receipt!.Body.Should().Contain("sin cebolla");
    }

    // ══════════════════════════════════════════════════
    // Order Intelligence Tests
    // ══════════════════════════════════════════════════

    // ── Menu catalog & normalization ──

    [Theory]
    [InlineData("hamburguesa", "Hamburguesa")]
    [InlineData("hamburguesita", "Hamburguesa")]
    [InlineData("burger", "Hamburguesa")]
    [InlineData("coca cola", "Coca Cola")]
    [InlineData("refresco", "Coca Cola")]
    [InlineData("gaseosa", "Coca Cola")]
    [InlineData("soda", "Coca Cola")]
    [InlineData("papas fritas", "Papas")]
    [InlineData("fritas", "Papas")]
    [InlineData("papitas", "Papas")]
    [InlineData("pizza", "Pizza")]
    [InlineData("sushi roll", "Sushi Roll")]
    [InlineData("sushi", "Sushi Roll")]
    [InlineData("combo", "Combo")]
    [InlineData("hot dog", "Hot Dog")]
    [InlineData("perro caliente", "Hot Dog")]
    [InlineData("tequenos", "Teque\u00f1os")]
    [InlineData("empanada", "Empanada")]
    [InlineData("jugo", "Jugo")]
    [InlineData("agua", "Agua")]
    [InlineData("cerveza", "Cerveza")]
    [InlineData("birra", "Cerveza")]
    public void NormalizeMenuItemName_Aliases(string input, string expected)
    {
        WebhookProcessor.NormalizeMenuItemName(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("hamburgesa")]   // typo
    [InlineData("hamburguesaz")] // typo
    [InlineData("hamburguea")]   // typo
    public void NormalizeMenuItemName_TypoTolerance(string input)
    {
        WebhookProcessor.NormalizeMenuItemName(input).Should().Be("Hamburguesa");
    }

    // ── ParseOrderText ──

    [Fact]
    public void ParseOrderText_MixedOrder_HamburgerWithPapasAndCoca()
    {
        var result = WebhookProcessor.ParseOrderText("3 hamburguesas con papas y coca");

        result.Should().Contain(p => p.Name == "Hamburguesa" && p.Quantity == 3);
        result.Should().Contain(p => p.Name == "Papas");
        result.Should().Contain(p => p.Name == "Coca Cola");
    }

    [Fact]
    public void ParseOrderText_MultipleItemsWithQuantities()
    {
        var result = WebhookProcessor.ParseOrderText("2 sushi roll y 1 coca");

        result.Should().Contain(p => p.Name == "Sushi Roll" && p.Quantity == 2);
        result.Should().Contain(p => p.Name == "Coca Cola" && p.Quantity == 1);
    }

    [Fact]
    public void ParseOrderText_WithModifiers()
    {
        var result = WebhookProcessor.ParseOrderText("1 hamburguesa sin cebolla extra queso");

        result.Should().Contain(p => p.Name == "Hamburguesa" && p.Quantity == 1);
        var burger = result.First(p => p.Name == "Hamburguesa");
        burger.Modifiers.Should().NotBeNullOrWhiteSpace();
        burger.Modifiers.Should().Contain("sin cebolla");
    }

    [Fact]
    public void ParseOrderText_ComboWithSides()
    {
        var result = WebhookProcessor.ParseOrderText("2 combos con papas y refresco");

        result.Should().Contain(p => p.Name == "Combo" && p.Quantity == 2);
        result.Should().Contain(p => p.Name == "Papas");
        result.Should().Contain(p => p.Name == "Coca Cola"); // refresco -> Coca Cola
    }

    [Fact]
    public void ParseOrderText_ConversationalNoise_StrippedCleanly()
    {
        var result = WebhookProcessor.ParseOrderText("hola quiero 2 hamburguesas por favor");

        result.Should().Contain(p => p.Name == "Hamburguesa" && p.Quantity == 2);
    }

    [Fact]
    public void ParseOrderText_PizzaWithHalfModifier()
    {
        var result = WebhookProcessor.ParseOrderText("1 pizza grande mitad pepperoni mitad jam\u00f3n");

        result.Should().Contain(p => p.Name == "Pizza");
        var pizza = result.First(p => p.Name == "Pizza");
        pizza.Modifiers.Should().Contain("mitad");
    }

    [Fact]
    public void ParseOrderText_SingleItemNoQuantity()
    {
        var result = WebhookProcessor.ParseOrderText("hamburguesa delivery");

        result.Should().Contain(p => p.Name == "Hamburguesa");
    }

    // ── Full flow integration tests ──

    [Fact]
    public async Task QuickParse_MixedOrder_ParsesAllItems()
    {
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "2 hamburguesas y 1 coca cola delivery"),
            _testBusiness);

        state.Items.Should().Contain(i => i.Name == "Hamburguesa" && i.Quantity == 2);
        state.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
        state.DeliveryType.Should().Be("delivery");
    }

    [Fact]
    public async Task QuickParse_WithModifiers_StoresModifiers()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "1 hamburguesa sin cebolla extra queso pickup"),
            _testBusiness);

        state.Items.Should().Contain(i => i.Name == "Hamburguesa");
        // Modifiers or special instructions should capture the "sin cebolla extra queso"
        var hasModifiers = state.Items.Any(i => !string.IsNullOrWhiteSpace(i.Modifiers));
        var hasObs = !string.IsNullOrWhiteSpace(state.SpecialInstructions);
        (hasModifiers || hasObs).Should().BeTrue("modifiers should be captured as item modifiers or observations");
    }

    [Fact]
    public async Task QuickParse_NewMenuItems_Recognized()
    {
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "2 pizzas y 3 cervezas delivery"),
            _testBusiness);

        state.Items.Should().Contain(i => i.Name == "Pizza" && i.Quantity == 2);
        state.Items.Should().Contain(i => i.Name == "Cerveza" && i.Quantity == 3);
    }

    [Fact]
    public async Task QuickParse_AliasResolution_RefrescoBecomescoca()
    {
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "1 refresco pickup"),
            _testBusiness);

        state.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    [Fact]
    public void SplitIntoOrderSegments_BasicSplit()
    {
        var parts = WebhookProcessor.SplitIntoOrderSegments("2 hamburguesas y 1 coca");
        parts.Should().HaveCountGreaterOrEqualTo(2);
    }

    [Fact]
    public void ExtractItemAndModifiers_WithSinCebolla()
    {
        var item = WebhookProcessor.ExtractItemAndModifiers("hamburguesa sin cebolla");
        item.Should().NotBeNull();
        item!.Name.Should().Be("Hamburguesa");
        item.Modifiers.Should().Contain("sin cebolla");
    }

    [Fact]
    public void ExtractItemAndModifiers_WithExtraQueso()
    {
        var item = WebhookProcessor.ExtractItemAndModifiers("hamburguesa extra queso");
        item.Should().NotBeNull();
        item!.Name.Should().Be("Hamburguesa");
        item.Modifiers.Should().Contain("extra queso");
    }

    [Fact]
    public void ExtractItemAndModifiers_UnknownItem_ReturnsNull()
    {
        var item = WebhookProcessor.ExtractItemAndModifiers("zapatos deportivos");
        item.Should().BeNull();
    }

    [Fact]
    public void FormatItemText_WithModifiers()
    {
        var entry = new ConversationItemEntry { Name = "Hamburguesa", Quantity = 2, Modifiers = "sin cebolla" };
        WebhookProcessor.FormatItemText(entry).Should().Be("2 Hamburguesa (sin cebolla)");
    }

    [Fact]
    public void FormatItemText_WithoutModifiers()
    {
        var entry = new ConversationItemEntry { Name = "Papas", Quantity = 3 };
        WebhookProcessor.FormatItemText(entry).Should().Be("3 Papas");
    }

    // ── Existing flows still work ──

    [Fact]
    public async Task ExistingDemoFlow_StillWorks_HamburguessaDelivery()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // Step 1: Order
        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "2 hamburguesas delivery"),
            _testBusiness);

        state.Items.Should().Contain(i => i.Name == "Hamburguesa" && i.Quantity == 2);
        state.DeliveryType.Should().Be("delivery");

        // Observation prompt should have been sent
        state.ObservationPromptSent.Should().BeTrue();
    }

    [Fact]
    public async Task ReceiptShowsModifiers_WhenPresent()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields
        {
            CheckoutFormSent = true,
            CustomerName = "Test",
            CustomerIdNumber = "12345678",
            CustomerPhone = "04141234567",
            Address = "Test address",
            PaymentMethod = "efectivo",
            GpsPinReceived = true,
            DeliveryType = "delivery",
            ObservationPromptSent = true,
            ObservationAnswered = true
        };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa", Quantity = 1, Modifiers = "sin cebolla" });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "confirmar"), _testBusiness);

        var receipt = sentMessages.FirstOrDefault(m => m.Body.Contains("PEDIDO CONFIRMADO"));
        receipt.Should().NotBeNull();
        receipt!.Body.Should().Contain("sin cebolla");
    }
}
