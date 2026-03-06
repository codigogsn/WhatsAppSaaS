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
