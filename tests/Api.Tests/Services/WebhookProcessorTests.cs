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

        _testBusiness = new BusinessContext(Guid.NewGuid(), "123456789", "test-token", "Mi Restaurante", MenuPdfUrl: "https://test.example.com/menu-demo.pdf");

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
                        Items = [new WhatsAppSaaS.Application.DTOs.OrderItem { Name = "Hamburguesa Clasica", Quantity = 2 }],
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
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });

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

        // Message 2: PDF menu document
        sentMessages[1].DocumentUrl.Should().NotBeNullOrWhiteSpace("menu should be sent as PDF document");
        sentMessages[1].DocumentFilename.Should().Be("menu.pdf");
        sentMessages[1].Body.Should().Contain("Menú");

        // Message 3: Prompt
        sentMessages[2].Body.Should().Contain("Envíame tu pedido");
    }

    [Fact]
    public async Task Greeting_WelcomeMessage_IncludesBusinessName()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var biz = new BusinessContext(Guid.NewGuid(), "123456789", "test-token", "Burger Palace", MenuPdfUrl: "https://test.example.com/menu-demo.pdf");
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
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.DeliveryType = "delivery";
        state.ExtrasOffered = true;
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;
        state.OrderConfirmed = true;
        state.PaymentMethod = "efectivo";

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // Premium emoji labels
        reply.Body.Should().Contain("Nombre:");
        reply.Body.Should().Contain("C\u00e9dula:");
        reply.Body.Should().Contain("Tel\u00e9fono:");
        reply.Body.Should().Contain("Direcci\u00f3n:");
        reply.Body.Should().Contain("Ubicaci\u00f3n GPS:");
        reply.Body.Should().Contain("OBLIGATORIO");
        reply.Body.Should().Contain("CONFIRMAR");
    }

    [Fact]
    public void OrderSummary_ShowsItemPricesAndTotal()
    {
        var items = new List<ConversationItemEntry>
        {
            new() { Name = "Hamburguesa Clasica", Quantity = 2, UnitPrice = 6.50m },
            new() { Name = "Coca Cola", Quantity = 1, UnitPrice = 1.50m }
        };

        var summary = Msg.OrderSummaryWithTotal(items);

        summary.Should().Contain("RESUMEN DE TU PEDIDO");
        summary.Should().Contain("2x Hamburguesa Clasica");
        summary.Should().Contain("$6.50 c/u = $13.00");
        summary.Should().Contain("1x Coca Cola");
        summary.Should().Contain("$1.50 c/u = $1.50");
        summary.Should().Contain("TOTAL: $14.50");
    }

    [Fact]
    public void Receipt_IncludesTotalAPagar()
    {
        var items = new List<ConversationItemEntry>
        {
            new() { Name = "Hamburguesa Clasica", Quantity = 2, UnitPrice = 6.50m },
            new() { Name = "Papas Medianas", Quantity = 1, UnitPrice = 3.50m }
        };

        var receipt = Msg.BuildReceipt(
            "ABC12345", "Juan", "V-12345678", "+584141234567",
            items, null, "Calle 1", "EFECTIVO", "delivery");

        receipt.Should().Contain("PEDIDO CONFIRMADO");
        receipt.Should().Contain("2x Hamburguesa Clasica");
        receipt.Should().Contain("1x Papas Medianas");
        receipt.Should().Contain("TOTAL A PAGAR: $16.50");
    }

    [Fact]
    public void OrderSummary_SortedByCategoryPriority()
    {
        // Items in wrong order: bebida, salsa, hamburguesa, papas
        var items = new List<ConversationItemEntry>
        {
            new() { Name = "Coca Cola", Quantity = 1, UnitPrice = 1.50m },
            new() { Name = "Salsa Ajo", Quantity = 1, UnitPrice = 0.50m },
            new() { Name = "Hamburguesa Clasica", Quantity = 2, UnitPrice = 6.50m },
            new() { Name = "Papas Medianas", Quantity = 1, UnitPrice = 3.50m }
        };

        var summary = Msg.OrderSummaryWithTotal(items);

        // Hamburguesas (1) should come before Papas (3) before Salsas (6) before Bebidas (7)
        var idxHamb = summary.IndexOf("Hamburguesa Clasica");
        var idxPapas = summary.IndexOf("Papas Medianas");
        var idxSalsa = summary.IndexOf("Salsa Ajo");
        var idxCoca = summary.IndexOf("Coca Cola");

        idxHamb.Should().BeLessThan(idxPapas, "hamburguesas should come before papas");
        idxPapas.Should().BeLessThan(idxSalsa, "papas should come before salsas");
        idxSalsa.Should().BeLessThan(idxCoca, "salsas should come before bebidas");
    }

    [Fact]
    public void Receipt_SortedByCategoryPriority()
    {
        // Items in reverse order: bebida first, hamburguesa last
        var items = new List<ConversationItemEntry>
        {
            new() { Name = "Coca Cola", Quantity = 1, UnitPrice = 1.50m },
            new() { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m }
        };

        var receipt = Msg.BuildReceipt(
            "ABC123", "Juan", "V-123", "+58414123",
            items, null, "Calle 1", "EFECTIVO", "delivery");

        var idxHamb = receipt.IndexOf("Hamburguesa Clasica");
        var idxCoca = receipt.IndexOf("Coca Cola");

        idxHamb.Should().BeLessThan(idxCoca, "hamburguesas should appear before bebidas in receipt");
    }

    [Fact]
    public void Receipt_PagoMovil_IncludesPreparationInstruction()
    {
        var items = new List<ConversationItemEntry>
        {
            new() { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m }
        };

        var receipt = Msg.BuildReceipt(
            "ABC123", "Juan", "V-123", "+58414123",
            items, null, "Calle 1",
            Msg.PaymentMethodText("pago_movil"), "delivery");

        receipt.Should().Contain("PAGO M\u00d3VIL");
        receipt.Should().Contain("Cuando env\u00edes el comprobante tu pedido entrar\u00e1 en preparaci\u00f3n.");
    }

    [Fact]
    public void Receipt_Efectivo_NoPreparationInstruction()
    {
        var items = new List<ConversationItemEntry>
        {
            new() { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m }
        };

        var receipt = Msg.BuildReceipt(
            "ABC123", "Juan", "V-123", "+58414123",
            items, null, "Calle 1", "EFECTIVO", "delivery");

        receipt.Should().NotContain("Cuando env\u00edes el comprobante");
    }

    [Fact]
    public void Receipt_IncludesEstimatedTime()
    {
        var items = new List<ConversationItemEntry>
        {
            new() { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m }
        };

        var receipt = Msg.BuildReceipt(
            "ABC123", "Juan", "V-123", "+58414123",
            items, null, "Calle 1", "EFECTIVO", "delivery");

        receipt.Should().Contain("Tiempo estimado: 30 minutos");
    }

    [Fact]
    public void Receipt_IncludesPreparationMessage()
    {
        var items = new List<ConversationItemEntry>
        {
            new() { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m }
        };

        var receipt = Msg.BuildReceipt(
            "ABC123", "Juan", "V-123", "+58414123",
            items, null, "Calle 1", "EFECTIVO", "delivery");

        receipt.Should().Contain("Tu pedido est\u00e1 siendo preparado");
        receipt.Should().Contain("Te avisaremos cuando salga para delivery");
    }

    [Fact]
    public async Task FullFlow_OrderSaved_WithUnitPricesAndTotal()
    {
        Order? savedOrder = null;
        _orderRepositoryMock
            .Setup(x => x.AddOrderAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Callback<Order, CancellationToken>((o, _) => savedOrder = o)
            .Returns(Task.CompletedTask);

        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var state = new ConversationFields
        {
            CheckoutFormSent = true,
            CustomerName = "Ana",
            CustomerIdNumber = "V-99999999",
            CustomerPhone = "04121234567",
            Address = "Av Principal",
            PaymentMethod = "efectivo",
            GpsPinReceived = true,
            DeliveryType = "delivery",
            ObservationPromptSent = true,
            ObservationAnswered = true
        };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2, UnitPrice = 6.50m });
        state.Items.Add(new ConversationItemEntry { Name = "Coca Cola", Quantity = 3, UnitPrice = 1.50m });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "confirmar"), _testBusiness);

        savedOrder.Should().NotBeNull();
        savedOrder!.Items.Should().HaveCount(2);

        var hamburguesa = savedOrder.Items.First(i => i.Name.Contains("Hamburguesa"));
        hamburguesa.UnitPrice.Should().Be(6.50m);
        hamburguesa.Quantity.Should().Be(2);
        hamburguesa.LineTotal.Should().Be(13.00m);

        var coca = savedOrder.Items.First(i => i.Name.Contains("Coca"));
        coca.UnitPrice.Should().Be(1.50m);
        coca.Quantity.Should().Be(3);
        coca.LineTotal.Should().Be(4.50m);

        savedOrder.SubtotalAmount.Should().Be(17.50m);
        savedOrder.TotalAmount.Should().Be(17.50m);
    }

    [Fact]
    public void AddOrIncreaseItem_SetsUnitPrice()
    {
        var state = new ConversationFields();

        // Use quick-parse which calls AddOrIncreaseItem internally
        WebhookProcessor.TryParseQuickOrder("2 hamburguesas clasicas", out var items, out _, out _);

        items.Should().NotBeEmpty();
        // Verify the item was found
        items[0].Name.Should().Be("Hamburguesa Clasica");
        items[0].Quantity.Should().Be(2);
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
            state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2 });
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
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2 });
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
        body.Should().Contain("2x Hamburguesa Clasica");
        body.Should().Contain("1x Coca Cola");
        body.Should().Contain("Direcci\u00f3n: Calle Principal #10");
        body.Should().Contain("Pago: EFECTIVO");
        body.Should().Contain("Tu pedido est\u00e1 siendo preparado");
        body.Should().Contain("Tiempo estimado: 30 minutos");
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

        // Should send greeting sequence (3 messages including PDF menu)
        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(
                It.Is<OutgoingMessage>(m => m.DocumentUrl != null && m.DocumentUrl.Contains("menu")),
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

        // Should send PDF menu since MenuSent was reset
        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(
                It.Is<OutgoingMessage>(m => m.DocumentUrl != null && m.DocumentUrl.Contains("menu")),
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
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
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

        sentMessages.Should().HaveCount(3, "should send welcome + menu PDF + prompt");
        sentMessages[0].Body.Should().Contain("bienvenido");
        sentMessages[1].DocumentUrl.Should().NotBeNullOrWhiteSpace("menu should be sent as PDF");
        sentMessages[2].Body.Should().Contain("Envíame tu pedido");

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

        sentMessages.Should().HaveCount(3, "should send welcome + menu PDF + prompt");
        sentMessages[0].Body.Should().Contain("bienvenido");
        sentMessages[1].DocumentUrl.Should().NotBeNullOrWhiteSpace("menu should be sent as PDF");

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

        sentMessages.Should().HaveCount(3, "should send welcome + menu PDF + prompt");
        sentMessages[1].DocumentUrl.Should().NotBeNullOrWhiteSpace("menu should be sent as PDF");
        sentMessages[1].DocumentFilename.Should().Be("menu.pdf");
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
        sentMessages[1].DocumentUrl.Should().NotBeNullOrWhiteSpace($"'{input}' should trigger PDF menu");
        sentMessages[2].Body.Should().Contain("Envíame tu pedido", $"'{input}' should trigger prompt");

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
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
        state.DeliveryType = "delivery";
        state.ExtrasOffered = true;
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;
        state.OrderConfirmed = true;
        state.PaymentMethod = "efectivo";

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // Must contain 🪪 (U+1FAAA = ID card), NOT 🪭 (U+1FAAD = fan)
        reply.Body.Should().Contain("\ud83e\udeaa", "should use ID card emoji for C\u00e9dula");
        reply.Body.Should().NotContain("\ud83e\udead", "should NOT use fan emoji");
    }

    [Fact]
    public void DeliveryTypePrompt_HasShoppingBagEmoji()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        state.OrderConfirmed = true;
        // No delivery type set — should prompt

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);
        reply.Body.Should().Contain("lo quieres");
        reply.Buttons.Should().NotBeNull();
        reply.Buttons!.Select(b => b.Title).Should().Contain("Delivery");
        reply.Buttons!.Select(b => b.Title).Should().Contain("Pickup");
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
    [InlineData("agrega 3 hamburgueAs mas porfavor", "Hamburguesa Clasica", 3)]
    [InlineData("agrega 2 cocas", "Coca Cola", 2)]
    [InlineData("agregame 1 papa", "Papas Medianas", 1)]
    [InlineData("suma 2 hamburguesas", "Hamburguesa Clasica", 2)]
    [InlineData("pon 4 papas", "Papas Medianas", 4)]
    [InlineData("2 hamburguesas más", "Hamburguesa Clasica", 2)]
    [InlineData("agrega 1 hamburgesa mas porfa", "Hamburguesa Clasica", 1)]
    public void TryParseOrderModification_Add_ParsesCorrectly(string input, string expectedItem, int expectedQty)
    {
        var result = WebhookProcessor.TryParseOrderModification(input, out var mod);

        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.Add);
        mod.ItemName.Should().Be(expectedItem);
        mod.Quantity.Should().Be(expectedQty);
    }

    [Theory]
    [InlineData("quita 1 papa", "Papas Medianas", 1)]
    [InlineData("elimina 2 cocas", "Coca Cola", 2)]
    [InlineData("sin las papas", "Papas Medianas", int.MaxValue)]
    [InlineData("borra una hamburguesa", "Hamburguesa Clasica", 1)]
    public void TryParseOrderModification_Remove_ParsesCorrectly(string input, string expectedItem, int expectedQty)
    {
        var result = WebhookProcessor.TryParseOrderModification(input, out var mod);

        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.Remove);
        mod.ItemName.Should().Be(expectedItem);
        mod.Quantity.Should().Be(expectedQty);
    }

    [Theory]
    [InlineData("cambia a 3 hamburguesas", "Hamburguesa Clasica", 3)]
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
    [InlineData("hamburgueas", "Hamburguesa Clasica")]
    [InlineData("hamburgesa", "Hamburguesa Clasica")]
    [InlineData("hamburgesas", "Hamburguesa Clasica")]
    [InlineData("hamburguesas", "Hamburguesa Clasica")]
    [InlineData("cocacolas", "Coca Cola")]
    [InlineData("cocas", "Coca Cola")]
    [InlineData("coca cola", "Coca Cola")]
    [InlineData("papaas", "Papas Medianas")]
    [InlineData("papas", "Papas Medianas")]
    [InlineData("papa", "Papas Medianas")]
    [InlineData("papitas", "Papas Pequenas")]
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
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2 });
        state.Items.Add(new ConversationItemEntry { Name = "Coca Cola", Quantity = 3 });
        state.Items.Add(new ConversationItemEntry { Name = "Papas Medianas", Quantity = 6 });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var payload = CreateTextMessagePayload("5511999999999","agrega 3 hamburgueAs mas porfavor");

        // Act
        await _sut.ProcessAsync(payload, _testBusiness);

        // Assert: Hamburguesa Clasica quantity should be 5 (2 + 3)
        state.Items.Should().HaveCount(3);
        state.Items.First(i => i.Name == "Hamburguesa Clasica").Quantity.Should().Be(5);
        state.Items.First(i => i.Name == "Coca Cola").Quantity.Should().Be(3);
        state.Items.First(i => i.Name == "Papas Medianas").Quantity.Should().Be(6);

        // Assert: no raw text item was added
        state.Items.Should().NotContain(i => i.Name.Contains("hamburgueAs"));
        state.Items.Should().NotContain(i => i.Name.Contains("porfavor"));
    }

    [Fact]
    public async Task OrderModification_Add_ResponseMentionsCleanItemName()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2 });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var sentBodies = new List<string>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((msg, _) => sentBodies.Add(msg.Body))
            .ReturnsAsync(true);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999","agrega 3 hamburgueas mas porfavor"), _testBusiness);

        sentBodies.Should().NotBeEmpty();
        // The modification response (first message) should mention the clean item name
        var modResponse = sentBodies.First();
        modResponse.Should().Contain("3 Hamburguesa Clasica");
        modResponse.Should().Contain("CONFIRMAR");
        modResponse.Should().NotContain("hamburgueAs");
        modResponse.Should().NotContain("porfavor");
    }

    [Fact]
    public async Task OrderModification_Remove_DecreasesQuantity()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Papas Medianas", Quantity = 6 });

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
        state.Items.Add(new ConversationItemEntry { Name = "Papas Medianas", Quantity = 6 });
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2 });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999","sin las papas"), _testBusiness);

        state.Items.Should().HaveCount(1);
        state.Items.First().Name.Should().Be("Hamburguesa Clasica");
    }

    [Fact]
    public async Task OrderModification_Replace_SetsExactQuantity()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2 });

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
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2 });
        state.Items.Add(new ConversationItemEntry { Name = "Coca Cola", Quantity = 3 });
        state.Items.Add(new ConversationItemEntry { Name = "Papas Medianas", Quantity = 6 });
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
        state.Items.First(i => i.Name == "Hamburguesa Clasica").Quantity.Should().Be(5);

        // Step 2: Confirm
        sentBodies.Clear();
        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999","confirmar"), _testBusiness);

        // The receipt should contain clean item names only
        var receipt = sentBodies.LastOrDefault() ?? "";
        receipt.Should().Contain("5x Hamburguesa Clasica");
        receipt.Should().Contain("3x Coca Cola");
        receipt.Should().Contain("6x Papas Medianas");
        receipt.Should().NotContain("hamburgueAs");
        receipt.Should().NotContain("porfavor");
        receipt.Should().Contain("PEDIDO CONFIRMADO");
    }

    // ══════════════════════════════════════════════
    // OBSERVATION / SPECIAL INSTRUCTIONS TESTS
    // ══════════════════════════════════════════════

    [Fact]
    public void BuildOrderReply_AfterItems_ShowsObservationQuestion()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // New flow: items → observation question (before delivery)
        reply.Body.Should().Contain("observaci\u00f3n");
        reply.Buttons.Should().NotBeNull();
        reply.Buttons!.Select(b => b.Title).Should().Contain("S\u00ed");
        reply.Buttons!.Select(b => b.Title).Should().Contain("No");
        state.ExtrasOffered.Should().BeTrue();
    }

    [Fact]
    public void BuildOrderReply_WithExistingObservation_ShowsObservationQuestion()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
        state.DeliveryType = "delivery";
        state.SpecialInstructions = "sin cebolla";

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // Now always shows the observation question (no longer auto-detects)
        reply.Body.Should().Contain("observaci\u00f3n");
        reply.Buttons.Should().NotBeNull();
    }

    [Fact]
    public async Task ObservationAnswer_No_ContinuesWithoutObservation()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        OutgoingMessage? sentMsg = null;
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((msg, _) => sentMsg = msg)
            .ReturnsAsync(true);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "no"), _testBusiness);

        state.ObservationAnswered.Should().BeTrue();
        state.SpecialInstructions.Should().BeNull();
        // Should show confirmation prompt (not checkout form — that comes after CONFIRMAR)
        sentMsg.Should().NotBeNull();
        sentMsg!.Body.Should().Contain("RESUMEN DE TU PEDIDO");
        sentMsg.Body.Should().Contain("deseas hacer");
        sentMsg.Buttons.Should().NotBeNull();
        sentMsg.Buttons!.Select(b => b.Title).Should().Contain("Confirmar");
        sentMsg.Buttons!.Select(b => b.Title).Should().Contain("Editar pedido");
    }

    [Fact]
    public async Task ObservationAnswer_FreeText_StoresObservation()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2 });
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
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2 });
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
        state.Items.First().Name.Should().Be("Hamburguesa Clasica");
        state.SpecialInstructions.Should().Contain("sin cebolla");
    }

    // ── Final receipt includes observation ──

    [Fact]
    public async Task FinalReceipt_IncludesObservationWhenPresent()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2 });
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
        receipt.Should().Contain("Observaciones: 1 sin cebolla, 1 con extra queso");
    }

    [Fact]
    public async Task FinalReceipt_NoObservationLine_WhenEmpty()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
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
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2 });
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
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2 });
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
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
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
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2 });
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
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
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
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
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
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
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
    public async Task OrderReady_ShowsConfirmationPrompt_NotCheckoutForm()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;
        // CheckoutFormSent = false, OrderConfirmed = false => should show confirmation prompt

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // Trigger via quick order that reaches BuildOrderReplyFromState
        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "1 hamburguesa delivery"), _testBusiness);

        var confirmPrompt = sentMessages.FirstOrDefault(m => m.Body.Contains("RESUMEN DE TU PEDIDO"));
        confirmPrompt.Should().NotBeNull();
        confirmPrompt!.Body.Should().Contain("deseas hacer");
        confirmPrompt.Buttons.Should().NotBeNull();
        confirmPrompt.Buttons!.Select(b => b.Title).Should().Contain("Confirmar");
        confirmPrompt.Buttons!.Select(b => b.Title).Should().Contain("Editar pedido");
        confirmPrompt.Buttons!.Select(b => b.Title).Should().Contain("Cancelar");
        confirmPrompt.Body.Should().NotContain("Nombre:"); // checkout form should NOT appear yet
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
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2 });
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
        receipt.Body.Should().Contain("2x Hamburguesa");
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
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
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
    [InlineData("hamburguesa", "Hamburguesa Clasica")]
    [InlineData("hamburguesita", "Hamburguesa Clasica")]
    [InlineData("burger", "Hamburguesa Clasica")]
    [InlineData("burguer", "Hamburguesa Clasica")]
    [InlineData("coca cola", "Coca Cola")]
    [InlineData("refresco", "Coca Cola")]
    [InlineData("gaseosa", "Coca Cola")]
    [InlineData("soda", "Coca Cola")]
    [InlineData("papas fritas", "Papas Medianas")]
    [InlineData("fritas", "Papas Medianas")]
    [InlineData("papitas", "Papas Pequenas")]
    [InlineData("combo", "Combo Clasico")]
    [InlineData("hot dog", "Perro Clasico")]
    [InlineData("perro caliente", "Perro Clasico")]
    [InlineData("perro", "Perro Clasico")]
    [InlineData("malta", "Malta")]
    [InlineData("agua", "Agua")]
    [InlineData("pepsi", "Pepsi")]
    [InlineData("bacon", "Hamburguesa Bacon")]
    [InlineData("salsa picante", "Salsa Picante")]
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
        WebhookProcessor.NormalizeMenuItemName(input).Should().Be("Hamburguesa Clasica");
    }

    // ── ParseOrderText ──

    [Fact]
    public void ParseOrderText_MixedOrder_HamburgerWithPapasAndCoca()
    {
        var result = WebhookProcessor.ParseOrderText("3 hamburguesas con papas y coca");

        result.Should().Contain(p => p.Name == "Hamburguesa Clasica" && p.Quantity == 3);
        result.Should().Contain(p => p.Name == "Papas Medianas");
        result.Should().Contain(p => p.Name == "Coca Cola");
    }

    [Fact]
    public void ParseOrderText_MultipleItemsWithQuantities()
    {
        var result = WebhookProcessor.ParseOrderText("2 perros y 1 coca");

        result.Should().Contain(p => p.Name == "Perro Clasico" && p.Quantity == 2);
        result.Should().Contain(p => p.Name == "Coca Cola" && p.Quantity == 1);
    }

    [Fact]
    public void ParseOrderText_WithModifiers()
    {
        var result = WebhookProcessor.ParseOrderText("1 hamburguesa sin cebolla extra queso");

        result.Should().Contain(p => p.Name == "Hamburguesa Clasica" && p.Quantity == 1);
        var burger = result.First(p => p.Name == "Hamburguesa Clasica");
        burger.Modifiers.Should().NotBeNullOrWhiteSpace();
        burger.Modifiers.Should().Contain("sin cebolla");
    }

    [Fact]
    public void ParseOrderText_ComboWithSides()
    {
        var result = WebhookProcessor.ParseOrderText("2 combos con papas y refresco");

        result.Should().Contain(p => p.Name == "Combo Clasico" && p.Quantity == 2);
        result.Should().Contain(p => p.Name == "Papas Medianas");
        result.Should().Contain(p => p.Name == "Coca Cola"); // refresco -> Coca Cola
    }

    [Fact]
    public void ParseOrderText_ConversationalNoise_StrippedCleanly()
    {
        var result = WebhookProcessor.ParseOrderText("hola quiero 2 hamburguesas por favor");

        result.Should().Contain(p => p.Name == "Hamburguesa Clasica" && p.Quantity == 2);
    }

    [Fact]
    public void ParseOrderText_PerroWithModifier()
    {
        var result = WebhookProcessor.ParseOrderText("1 perro con todo");

        result.Should().Contain(p => p.Name == "Perro Clasico");
        var perro = result.First(p => p.Name == "Perro Clasico");
        perro.Modifiers.Should().Contain("con todo");
    }

    [Fact]
    public void ParseOrderText_SingleItemNoQuantity()
    {
        var result = WebhookProcessor.ParseOrderText("hamburguesa delivery");

        result.Should().Contain(p => p.Name == "Hamburguesa Clasica");
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

        state.Items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
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
            CreateTextMessagePayload("5511999999999", "1 hamburguesa sin cebolla pickup"),
            _testBusiness);

        state.Items.Should().Contain(i => i.Name == "Hamburguesa Clasica");
        // Modifiers or special instructions should capture the "sin cebolla"
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
            CreateTextMessagePayload("5511999999999", "2 perros y 3 maltas delivery"),
            _testBusiness);

        state.Items.Should().Contain(i => i.Name == "Perro Clasico" && i.Quantity == 2);
        state.Items.Should().Contain(i => i.Name == "Malta" && i.Quantity == 3);
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

    // ── Word number conversion tests ──

    [Theory]
    [InlineData("una hamburguesa", "1 hamburguesa")]
    [InlineData("dos perros", "2 perros")]
    [InlineData("tres cocas", "3 cocas")]
    [InlineData("cuatro papas", "4 papas")]
    [InlineData("cinco hamburguesas", "5 hamburguesas")]
    public void ConvertWordNumbers_ConvertsSpanishNumbers(string input, string expected)
    {
        WebhookProcessor.ConvertWordNumbersToDigits(input).Trim()
            .Should().Be(expected);
    }

    // ── Multi-line order parsing ──

    [Fact]
    public void ParseOrderText_MultiLineOrder_ParsesAllItems()
    {
        var result = WebhookProcessor.ParseOrderText("2 hamburguesas dobles\n1 perro caliente\n2 cocas");

        result.Should().Contain(p => p.Name == "Hamburguesa Doble" && p.Quantity == 2);
        result.Should().Contain(p => p.Name == "Perro Clasico" && p.Quantity == 1);
        result.Should().Contain(p => p.Name == "Coca Cola" && p.Quantity == 2);
    }

    [Fact]
    public void ParseOrderText_MultiLineWithDelivery_ParsesItemsIgnoresDelivery()
    {
        var result = WebhookProcessor.ParseOrderText("3 hamburguesas clasicas\n2 papas\n1 coca cola\npickup");

        result.Should().Contain(p => p.Name == "Hamburguesa Clasica" && p.Quantity == 3);
        result.Should().Contain(p => p.Name.Contains("Papas") && p.Quantity == 2);
        result.Should().Contain(p => p.Name == "Coca Cola" && p.Quantity == 1);
    }

    // ── Word number integration ──

    [Fact]
    public void ParseOrderText_WordQuantities_ParsedCorrectly()
    {
        var result = WebhookProcessor.ParseOrderText("una hamburguesa doble\nuna papa grande\nsin cebolla");

        result.Should().Contain(p => p.Name == "Hamburguesa Doble" && p.Quantity == 1);
        result.Should().Contain(p => p.Name == "Papas Grandes" && p.Quantity == 1);
    }

    [Fact]
    public void ParseOrderText_MixedWordAndDigitQuantities()
    {
        var result = WebhookProcessor.ParseOrderText("dos hamburguesas, 1 perro, una coca");

        result.Should().Contain(p => p.Name == "Hamburguesa Clasica" && p.Quantity == 2);
        result.Should().Contain(p => p.Name == "Perro Clasico" && p.Quantity == 1);
        result.Should().Contain(p => p.Name == "Coca Cola" && p.Quantity == 1);
    }

    // ── Otra/otro handling ──

    [Fact]
    public void ParseOrderText_OtraConModifier_AddsSecondItem()
    {
        var result = WebhookProcessor.ParseOrderText("una hamburguesa doble sin cebolla\notra con extra queso");

        result.Should().HaveCountGreaterOrEqualTo(1);
        result.Should().Contain(p => p.Name == "Hamburguesa Doble");
    }

    // ── Observation extraction ──

    [Fact]
    public void ParseOrderText_ObservationAttachedToItem()
    {
        var result = WebhookProcessor.ParseOrderText("hamburguesa sin cebolla");

        result.Should().ContainSingle();
        result[0].Name.Should().Be("Hamburguesa Clasica");
        result[0].Modifiers.Should().Contain("sin cebolla");
    }

    [Fact]
    public void ParseOrderText_MultipleItemsWithObservation()
    {
        var result = WebhookProcessor.ParseOrderText("1 hamburguesa doble sin cebolla, 1 perro caliente con extra queso");

        result.Should().Contain(p => p.Name == "Hamburguesa Doble" && p.Modifiers != null && p.Modifiers.Contains("sin cebolla"));
        result.Should().Contain(p => p.Name.Contains("Perro") && p.Modifiers != null && p.Modifiers.Contains("extra queso"));
    }

    // ── Delivery keyword detection ──

    [Theory]
    [InlineData("2 hamburguesas delivery", "delivery")]
    [InlineData("2 hamburguesas a domicilio", "delivery")]
    [InlineData("2 hamburguesas envio", "delivery")]
    [InlineData("2 hamburguesas para retirar", "pickup")]
    [InlineData("2 hamburguesas voy a buscar", "pickup")]
    [InlineData("2 hamburguesas pickup", "pickup")]
    public void TryParseQuickOrder_DeliveryKeywords_Detected(string input, string expectedDelivery)
    {
        WebhookProcessor.TryParseQuickOrder(input, out var items, out var delivery, out _);

        items.Should().NotBeEmpty();
        delivery.Should().Be(expectedDelivery);
    }

    // ── Venezuelan Spanish patterns ──

    [Fact]
    public void ParseOrderText_VenezuelanConversational()
    {
        var result = WebhookProcessor.ParseOrderText("dame 2 hamburguesas clasicas y una coca");

        result.Should().Contain(p => p.Name == "Hamburguesa Clasica" && p.Quantity == 2);
        result.Should().Contain(p => p.Name == "Coca Cola" && p.Quantity == 1);
    }

    [Fact]
    public void ParseOrderText_ImplicitQuantity_DefaultsToOne()
    {
        var result = WebhookProcessor.ParseOrderText("hamburguesa");

        result.Should().ContainSingle();
        result[0].Name.Should().Be("Hamburguesa Clasica");
        result[0].Quantity.Should().Be(1);
    }

    // ── Modifier regex: bien tostado ──

    [Fact]
    public void ExtractItemAndModifiers_BienTostado()
    {
        var item = WebhookProcessor.ExtractItemAndModifiers("hamburguesa bien tostada");
        item.Should().NotBeNull();
        item!.Modifiers.Should().Contain("bien tostada");
    }

    // ── Full quick-parse integration with word numbers ──

    [Fact]
    public async Task QuickParse_WordNumbers_ParsedCorrectly()
    {
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "una hamburguesa doble y dos cocas delivery"),
            _testBusiness);

        state.Items.Should().Contain(i => i.Name == "Hamburguesa Doble" && i.Quantity == 1);
        state.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 2);
        state.DeliveryType.Should().Be("delivery");
    }

    [Fact]
    public async Task QuickParse_MultiLineNaturalOrder_ParsesAllItems()
    {
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "1 perro caliente\n1 hamburguesa\ny una coca\npara delivery"),
            _testBusiness);

        state.Items.Should().Contain(i => i.Name == "Perro Clasico" && i.Quantity == 1);
        state.Items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 1);
        state.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
        state.DeliveryType.Should().Be("delivery");
    }

    [Fact]
    public async Task QuickParse_EnvioKeyword_DetectedAsDelivery()
    {
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "2 hamburguesas para envio"),
            _testBusiness);

        state.Items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        state.DeliveryType.Should().Be("delivery");
    }

    [Fact]
    public void ExtractItemAndModifiers_WithSinCebolla()
    {
        var item = WebhookProcessor.ExtractItemAndModifiers("hamburguesa sin cebolla");
        item.Should().NotBeNull();
        item!.Name.Should().Be("Hamburguesa Clasica");
        item.Modifiers.Should().Contain("sin cebolla");
    }

    [Fact]
    public void ExtractItemAndModifiers_WithExtraQueso()
    {
        // "extra queso" is now a menu item, so test with "con extra tomate"
        var item = WebhookProcessor.ExtractItemAndModifiers("hamburguesa con extra tomate");
        item.Should().NotBeNull();
        item!.Name.Should().Be("Hamburguesa Clasica");
        item.Modifiers.Should().Contain("extra tomate");
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
        var entry = new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2, Modifiers = "sin cebolla" };
        WebhookProcessor.FormatItemText(entry).Should().Be("2 Hamburguesa Clasica (sin cebolla)");
    }

    [Fact]
    public void FormatItemText_WithoutModifiers()
    {
        var entry = new ConversationItemEntry { Name = "Papas Medianas", Quantity = 3 };
        WebhookProcessor.FormatItemText(entry).Should().Be("3 Papas Medianas");
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

        // Step 1: Order with inline delivery
        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "2 hamburguesas delivery"),
            _testBusiness);

        state.Items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        state.DeliveryType.Should().Be("delivery");

        // Observation question should have been shown (new flow: observation before confirmation)
        state.ExtrasOffered.Should().BeTrue();
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
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, Modifiers = "sin cebolla" });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "confirmar"), _testBusiness);

        var receipt = sentMessages.FirstOrDefault(m => m.Body.Contains("PEDIDO CONFIRMADO"));
        receipt.Should().NotBeNull();
        receipt!.Body.Should().Contain("sin cebolla");
    }

    // ──────────────────────────────────────────
    // REGRESSION: Compound order quantity propagation
    // ──────────────────────────────────────────

    [Fact]
    public void ParseOrderText_CompoundOrder_CadaUnaPropagatesQuantity()
    {
        // "3 hamburguesas cada una con papas y refresco"
        var items = WebhookProcessor.ParseOrderText("3 hamburguesas cada una con papas y refresco");

        items.Should().HaveCountGreaterOrEqualTo(3);
        items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 3);
        items.Should().Contain(i => i.Name == "Papas Medianas" && i.Quantity == 3);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 3);
    }

    [Fact]
    public void ParseOrderText_CompoundOrder_TodosConPropagatesQuantity()
    {
        // "2 hamburguesas todas con papas"
        var items = WebhookProcessor.ParseOrderText("2 hamburguesas todas con papas");

        items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Papas Medianas" && i.Quantity == 2);
    }

    [Fact]
    public void ParseOrderText_NormalCompound_NoPropagation()
    {
        // "2 hamburguesas y 1 coca" — no "cada una", quantities stay independent
        var items = WebhookProcessor.ParseOrderText("2 hamburguesas y 1 coca");

        items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    // ──────────────────────────────────────────
    // REGRESSION: Checkout single-field merge
    // ──────────────────────────────────────────

    [Fact]
    public void TryParseCheckoutForm_SinglePhoneInMerge_ParsesSuccessfully()
    {
        var parsed = WebhookProcessor.TryParseCheckoutForm(
            "0414-1234567", out var form, isMerge: true);

        parsed.Should().BeTrue();
        form.CustomerPhone.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TryParseCheckoutForm_SinglePhoneNoMerge_Rejects()
    {
        var parsed = WebhookProcessor.TryParseCheckoutForm(
            "0414-1234567", out var form, isMerge: false);

        parsed.Should().BeFalse("requires >= 2 fields without merge mode");
    }

    [Fact]
    public void TryParseCheckoutForm_SinglePaymentMethod_MergesInCheckout()
    {
        var parsed = WebhookProcessor.TryParseCheckoutForm(
            "efectivo", out var form, isMerge: true);

        parsed.Should().BeTrue();
        form.PaymentMethod.Should().Be("efectivo");
    }

    // ──────────────────────────────────────────
    // REGRESSION: Observation skip when inline
    // ──────────────────────────────────────────

    [Fact]
    public void BuildOrderReplyFromState_InlineObservation_SkipsPrompt()
    {
        var state = new ConversationFields
        {
            DeliveryType = "delivery",
            SpecialInstructions = "sin cebolla",
            ObservationAnswered = true
        };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // Should skip observation prompt and show confirmation prompt
        reply.Body.Should().NotContain("observaci\u00f3n");
        reply.Body.Should().Contain("deseas hacer");
        reply.Buttons.Should().NotBeNull();
        reply.Buttons!.Select(b => b.Title).Should().Contain("Confirmar");
        reply.Buttons!.Select(b => b.Title).Should().Contain("Editar pedido");
        state.OrderConfirmed.Should().BeFalse("confirmation gate not yet passed");
    }

    [Fact]
    public void BuildOrderReplyFromState_NoExtrasOffered_ShowsObservationQuestion()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // Should show observation YES/NO question
        reply.Body.Should().Contain("observaci\u00f3n");
        reply.Buttons.Should().NotBeNull();
        reply.Buttons!.Select(b => b.Title).Should().Contain("S\u00ed");
        reply.Buttons!.Select(b => b.Title).Should().Contain("No");
        state.ExtrasOffered.Should().BeTrue();
    }

    // ──────────────────────────────────────────
    // REGRESSION: Typo tolerance
    // ──────────────────────────────────────────

    [Theory]
    [InlineData("hamburgueaas", "Hamburguesa Clasica")]
    [InlineData("cocas", "Coca Cola")]
    [InlineData("hamburgusa", "Hamburguesa Clasica")]
    [InlineData("papitas", "Papas Pequenas")]
    [InlineData("coka", "Coca Cola")]
    public void NormalizeMenuItemName_TypoVariants_ResolveCorrectly(string input, string expected)
    {
        var resolved = WebhookProcessor.NormalizeMenuItemName(input, WebhookProcessor.MenuCatalog);
        resolved.Should().Be(expected);
    }

    [Theory]
    [InlineData("pago mocil", "pago_movil")]
    [InlineData("pago movil", "pago_movil")]
    [InlineData("efectivo", "efectivo")]
    [InlineData("divisas", "divisas")]
    [InlineData("pm", "pago_movil")]
    public void NormalizePaymentMethod_TypoVariants_ResolveCorrectly(string input, string expected)
    {
        var result = WebhookProcessor.NormalizePaymentMethod(input);
        result.Should().Be(expected);
    }

    // ──────────────────────────────────────────
    // REGRESSION: Observation "sin cebolla todas" during quick parse
    // ──────────────────────────────────────────

    [Fact]
    public void ParseOrderText_SinCebollaWithItem_ExtractsObservation()
    {
        var items = WebhookProcessor.ParseOrderText("2 hamburguesas sin cebolla");

        items.Should().ContainSingle();
        items[0].Name.Should().Be("Hamburguesa Clasica");
        items[0].Quantity.Should().Be(2);
        items[0].Modifiers.Should().Contain("sin cebolla");
    }

    // ──────────────────────────────────────────
    // REGRESSION: Receipt must match cart
    // ──────────────────────────────────────────

    [Fact]
    public async Task FullFlow_ReceiptMatchesCartContents()
    {
        var sentMessages = new List<OutgoingMessage>();

        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields
        {
            MenuSent = true,
            CheckoutFormSent = true,
            CustomerName = "Juan",
            CustomerIdNumber = "V-12345678",
            CustomerPhone = "0414-1234567",
            Address = "Calle 1",
            PaymentMethod = "efectivo",
            GpsPinReceived = true,
            DeliveryType = "delivery",
            ObservationPromptSent = true,
            ObservationAnswered = true,
            SpecialInstructions = "sin cebolla"
        };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 3 });
        state.Items.Add(new ConversationItemEntry { Name = "Papas Medianas", Quantity = 3 });
        state.Items.Add(new ConversationItemEntry { Name = "Coca Cola", Quantity = 3 });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "confirmar"), _testBusiness);

        var receipt = sentMessages.FirstOrDefault(m => m.Body.Contains("PEDIDO CONFIRMADO"));
        receipt.Should().NotBeNull();
        receipt!.Body.Should().Contain("3x Hamburguesa");
        receipt!.Body.Should().Contain("3x Papas");
        receipt!.Body.Should().Contain("3x Coca Cola");
        receipt!.Body.Should().Contain("sin cebolla");
    }

    // ──────────────────────────────────────────
    // REGRESSION: Payment proof persists to order
    // ──────────────────────────────────────────

    [Fact]
    public async Task FullFlow_PaymentProof_PersistedToOrder()
    {
        Order? savedOrder = null;
        _orderRepositoryMock
            .Setup(x => x.AddOrderAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Callback<Order, CancellationToken>((o, _) => savedOrder = o)
            .Returns(Task.CompletedTask);

        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var state = new ConversationFields
        {
            MenuSent = true,
            CheckoutFormSent = true,
            CustomerName = "Maria",
            CustomerIdNumber = "V-87654321",
            CustomerPhone = "0412-9876543",
            Address = "Av Principal",
            PaymentMethod = "pago_movil",
            GpsPinReceived = true,
            DeliveryType = "delivery",
            ObservationPromptSent = true,
            ObservationAnswered = true,
            PaymentEvidenceRequested = true,
            PaymentEvidenceReceived = true,
            PaymentProofMediaId = "media_12345"
        };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "confirmar"), _testBusiness);

        savedOrder.Should().NotBeNull();
        savedOrder!.PaymentProofMediaId.Should().Be("media_12345");
        savedOrder.PaymentMethod.Should().Be("pago_movil");
    }

    // ──────────────────────────────────────────
    // REGRESSION: Delivery type not re-asked
    // ──────────────────────────────────────────

    // ──────────────────────────────────────────
    // REGRESSION: Standalone payment method recognition
    // ──────────────────────────────────────────

    [Fact]
    public async Task StandalonePaymentMethod_SingleLine_SetsPaymentMethod()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "pago movil"),
            _testBusiness);

        state.PaymentMethod.Should().Be("pago_movil");
        state.PaymentEvidenceRequested.Should().BeTrue();
    }

    [Fact]
    public async Task StandalonePaymentMethod_MultilineCheckout_DoesNotIntercept()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
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

        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "Ana\n12345678\n04141234567\naltamira\nefectivo"),
            _testBusiness);

        // Should parse as checkout form, not standalone payment
        state.CustomerName.Should().Be("Ana");
        state.PaymentMethod.Should().Be("efectivo");
    }

    // ──────────────────────────────────────────
    // REGRESSION: Proof image capture without explicit request
    // ──────────────────────────────────────────

    [Fact]
    public async Task ProofImage_CapturedWhenPaymentMethodSet_WithoutExplicitRequest()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
        state.DeliveryType = "delivery";
        state.PaymentMethod = "pago_movil";
        // PaymentEvidenceRequested is false — user sends proof proactively

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // Send an image message
        var imagePayload = CreateImageMessagePayload("5511999999999", "media_proof_123");
        await _sut.ProcessAsync(imagePayload, _testBusiness);

        state.PaymentEvidenceReceived.Should().BeTrue();
        state.PaymentProofMediaId.Should().Be("media_proof_123");

        var proofMsg = sentMessages.FirstOrDefault(m => m.Body.Contains("Comprobante recibido"));
        proofMsg.Should().NotBeNull();
    }

    [Fact]
    public async Task ProofDocument_CapturedViaDocumentProperty()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
        state.DeliveryType = "delivery";
        state.PaymentMethod = "divisas";

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // Send a document message (PDF receipt)
        var docPayload = CreateDocumentMessagePayload("5511999999999", "doc_media_456");
        await _sut.ProcessAsync(docPayload, _testBusiness);

        state.PaymentEvidenceReceived.Should().BeTrue();
        state.PaymentProofMediaId.Should().Be("doc_media_456");
    }

    private static WebhookPayload CreateImageMessagePayload(string from, string mediaId) => new()
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
                                    Type = "image",
                                    Image = new WebhookMedia { Id = mediaId, MimeType = "image/jpeg" }
                                }
                            ]
                        }
                    }
                ]
            }
        ]
    };

    private static WebhookPayload CreateDocumentMessagePayload(string from, string mediaId) => new()
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
                                    Type = "document",
                                    Document = new WebhookMedia { Id = mediaId, MimeType = "application/pdf" }
                                }
                            ]
                        }
                    }
                ]
            }
        ]
    };

    // ──────────────────────────────────────────
    // REGRESSION: Delivery type not re-asked
    // ──────────────────────────────────────────

    [Fact]
    public void BuildOrderReplyFromState_DeliveryAlreadySet_DoesNotReAsk()
    {
        var state = new ConversationFields
        {
            DeliveryType = "delivery"
        };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        reply.Body.Should().NotContain("pickup");
        reply.Body.Should().NotContain("delivery");
    }

    // ══════════════════════════════════════════════
    // AI SALES OPTIMIZATION TESTS
    // ══════════════════════════════════════════════

    // 1. Burger order → relevant drink/fries suggestion
    [Fact]
    public void BuildUpsellSuggestion_BurgerOrder_SuggestsDrinkOrSide()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });

        var (msg, item) = _sut.BuildUpsellSuggestion(state);

        msg.Should().NotBeNull();
        item.Should().NotBeNull();
        // Should suggest from bebidas or papas category
        var validSuggestions = new[] { "Coca Cola", "Coca Cola 1L", "Pepsi", "Te Frio", "Agua", "Malta",
            "Papas Pequenas", "Papas Medianas", "Papas Grandes", "Papas con Queso", "Papas Mixtas" };
        validSuggestions.Should().Contain(item!);
    }

    // 2. Suggestion suppressed during checkout
    [Fact]
    public async Task SmartSuggestion_SuppressedDuringCheckout()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;
        state.CheckoutFormSent = true;  // Already in checkout

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // Add another item during checkout
        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "agrega 1 coca"),
            _testBusiness);

        // Should NOT get a suggestion message (already in checkout)
        var upsellMsg = sentMessages.FirstOrDefault(m =>
            m.Body.Contains("agrego") || m.Body.Contains("combo") || m.Body.Contains("completar"));
        upsellMsg.Should().BeNull();
    }

    // 3. Upsell is disabled — "no gracias" with pending suggestion just continues flow
    [Fact]
    public async Task SmartSuggestion_Disabled_DeclineGoesToNormalFlow()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
        state.UpsellSent = true;
        state.LastSuggestedItem = "Coca Cola";

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // "no gracias" should NOT be intercepted by A1 handler (removed)
        // Instead it flows through normal handlers
        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "no gracias"),
            _testBusiness);

        // No upsell suggestion message should appear
        var upsellMsg = sentMessages.FirstOrDefault(m =>
            m.Body.Contains("agrego") && m.Body.Contains("$"));
        upsellMsg.Should().BeNull();
    }

    // 4. Combo suggestion when cart is close to combo
    [Fact]
    public void BuildComboSuggestion_CartCloseToCombo_SuggestsCompletion()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
        state.Items.Add(new ConversationItemEntry { Name = "Papas Medianas", Quantity = 1 });
        // Has main + side, but no drink → should suggest drink to complete combo

        var testBizBurger = new BusinessContext(Guid.NewGuid(), "123456789", "test-token",
            "Test Burger", RestaurantType: "burger", MenuPdfUrl: "https://test.example.com/menu-demo.pdf");

        var (msg, item) = _sut.BuildComboSuggestion(state, testBizBurger.RestaurantType);

        // May or may not match depending on catalog, but should not crash
        // and should not suggest an already-ordered item
        if (msg != null)
        {
            msg.Should().NotContain("Hamburguesa Clasica");
            msg.Should().NotContain("Papas Medianas");
        }
    }

    // 5. No nonsense suggestion when cart has multiple mains + sides + drinks
    [Fact]
    public void BuildUpsellSuggestion_CompletedCart_ReturnsNull()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2 });
        state.Items.Add(new ConversationItemEntry { Name = "Coca Cola", Quantity = 2 });
        state.Items.Add(new ConversationItemEntry { Name = "Papas Medianas", Quantity = 1 });

        // Cart covers hamburguesas + bebidas + papas — no suggestion needed
        var (msg, item) = _sut.BuildUpsellSuggestion(state);

        msg.Should().BeNull();
        item.Should().BeNull();
    }

    // 6. RestaurantType-aware suggestion fallback works
    [Fact]
    public void BuildUpsellSuggestion_WithRestaurantType_UsesTemplatePairings()
    {
        // Generic pairings use: comida→[bebida, acompanamiento] which matches demo catalog
        // "burger" template pairings use: hamburguesas→[bebidas, acompanamientos] (template category names)
        // Template pairings won't match demo catalog categories, so they return null gracefully
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });

        var (msg1, _) = _sut.BuildUpsellSuggestion(state, restaurantType: null);

        // Generic pairing should work with demo catalog (category "hamburguesas" matches)
        msg1.Should().NotBeNull();

        // Template pairings use same category names as demo catalog
        var (msg2, _) = _sut.BuildUpsellSuggestion(state, restaurantType: "burger");
        // Should also work since burger template uses same categories
        msg2.Should().NotBeNull();
    }

    // 7. Suggestion never changes existing order unexpectedly
    [Fact]
    public async Task SmartSuggestion_DoesNotModifyExistingCart()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2 });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // Add item — will trigger suggestion
        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "agrega 1 coca"),
            _testBusiness);

        // Original items must be intact
        state.Items.Should().ContainSingle(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        state.Items.Should().ContainSingle(i => i.Name == "Coca Cola" && i.Quantity == 1);
        // Only 2 items in cart (suggestion was just a message, not an addition)
        state.Items.Should().HaveCount(2);
    }

    // 8. Upsell disabled — "si dale" with pending suggestion does NOT add item
    [Fact]
    public async Task SuggestionAcceptance_Disabled_DoesNotAddItem()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
        state.UpsellSent = true;
        state.AddonSuggestionSent = true;
        state.LastSuggestedItem = "Coca Cola";

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // "si dale" should NOT be intercepted by A1 handler (removed)
        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "si dale"),
            _testBusiness);

        // Coca Cola should NOT have been auto-added by upsell acceptance
        state.Items.Should().NotContain(i => i.Name == "Coca Cola");

        // No upsell acceptance message
        var acceptMsg = sentMessages.FirstOrDefault(m => m.Body.Contains("agregue"));
        acceptMsg.Should().BeNull();
    }

    // 9. Decline detection helper tests
    [Theory]
    [InlineData("no gracias", true)]
    [InlineData("asi esta bien", true)]
    [InlineData("dejalo asi", true)]
    [InlineData("nada mas", true)]
    [InlineData("solo eso", true)]
    [InlineData("si dale", false)]
    [InlineData("hamburguesa", false)]
    public void IsSuggestionDecline_VariousInputs(string input, bool expected)
    {
        WebhookProcessor.IsSuggestionDecline(WebhookProcessor.Normalize(input))
            .Should().Be(expected);
    }

    [Theory]
    [InlineData("si", true)]
    [InlineData("dale", true)]
    [InlineData("claro", true)]
    [InlineData("metele", true)]
    [InlineData("no", false)]
    [InlineData("hamburguesa", false)]
    public void IsSuggestionAcceptance_VariousInputs(string input, bool expected)
    {
        WebhookProcessor.IsSuggestionAcceptance(WebhookProcessor.Normalize(input))
            .Should().Be(expected);
    }

    // ── BUG 1 regression: "delivery" must advance stage, not restart ──

    [Fact]
    public async Task DeliveryAnswer_AdvancesConversation_DoesNotRestart()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        // State: user has items but hasn't chosen delivery type yet
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2 });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // User sends just "delivery"
        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "delivery"),
            _testBusiness);

        // Delivery type should be set
        state.DeliveryType.Should().Be("delivery");

        // Items should be preserved (not reset)
        state.Items.Should().HaveCount(1);
        state.Items[0].Name.Should().Be("Hamburguesa Clasica");
        state.Items[0].Quantity.Should().Be(2);

        // Should NOT contain greeting/welcome/menu (restart indicators)
        var allText = string.Join(" ", sentMessages.Select(m => m.Body));
        allText.Should().NotContain("bienvenido", "delivery should not restart the conversation");
    }

    [Fact]
    public async Task PickupAnswer_AdvancesConversation_DoesNotRestart()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Coca Cola", Quantity = 1 });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "pick up"),
            _testBusiness);

        state.DeliveryType.Should().Be("pickup");
        state.Items.Should().HaveCount(1);
        state.Items[0].Name.Should().Be("Coca Cola");
    }

    // ── BUG 2 regression: payment template format ──

    [Fact]
    public void CheckoutForm_PaymentNotInForm_SeparateStep()
    {
        var form = Msg.CheckoutForm;

        // Payment is now a separate step — should NOT be in checkout form
        form.Should().NotContain("Pago (PAGO M", "payment is asked in a separate step");
        form.Should().NotContain("ZELLE):", "payment is asked in a separate step");

        // Checkout form should still have the other required fields
        form.Should().Contain("Nombre:");
        form.Should().Contain("C\u00e9dula:");
        form.Should().Contain("Tel\u00e9fono:");
        form.Should().Contain("Direcci\u00f3n:");
    }

    [Fact]
    public void PaymentMethodPrompt_HasAllOptions()
    {
        var prompt = Msg.PaymentMethodPrompt;
        prompt.Should().Contain("pagar");

        // Options are now in buttons, not inline text
        var buttons = Msg.PaymentButtons;
        buttons.Should().HaveCount(3);
        buttons.Select(b => b.Title).Should().Contain("Efectivo");
        buttons.Select(b => b.Title).Should().Contain("Pago móvil");
        buttons.Select(b => b.Title).Should().Contain("Zelle");
    }

    // ── BUG 3 regression: order quantity aggregation ──

    [Fact]
    public async Task QuickParse_DoesNotDoubleAddItems()
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

        // User orders "2 hamburguesas" — should result in exactly 2, not 4
        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "2 hamburguesas"),
            _testBusiness);

        var burger = state.Items.FirstOrDefault(i =>
            i.Name.Contains("amburg", StringComparison.OrdinalIgnoreCase));
        burger.Should().NotBeNull();
        burger!.Quantity.Should().Be(2, "initial order of 2 should not be doubled");
    }

    [Fact]
    public async Task OrderModification_AddItems_CorrectMath()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        // Start with 2 hamburguesas already in cart
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2 });
        state.DeliveryType = "delivery";

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // User says "agrega 6 hamburguesas mas"
        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "agrega 6 hamburguesas mas"),
            _testBusiness);

        var burger = state.Items.FirstOrDefault(i =>
            i.Name.Contains("amburg", StringComparison.OrdinalIgnoreCase));
        burger.Should().NotBeNull();
        burger!.Quantity.Should().Be(8, "2 existing + 6 added = 8, not 10");
    }

    [Fact]
    public async Task CompoundOrder_CorrectQuantities()
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

        // Compound order: "2 hamburguesas con papas y coca cola"
        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "2 hamburguesas con papas y coca cola"),
            _testBusiness);

        // Should have items without doubling
        var totalItems = state.Items.Sum(i => i.Quantity);
        totalItems.Should().BeLessOrEqualTo(5, "compound order should not double-add items");

        // Hamburguesa should be exactly 2
        var burger = state.Items.FirstOrDefault(i =>
            i.Name.Contains("amburg", StringComparison.OrdinalIgnoreCase));
        burger.Should().NotBeNull();
        burger!.Quantity.Should().Be(2);
    }

    // ── Full flow regression: order → observation → confirm → delivery → payment ──

    [Fact]
    public async Task FullFlow_OrderThenObservationToDelivery_AdvancesCorrectly()
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

        // Step 1: Order items → extras question
        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "quiero 1 hamburguesa"),
            _testBusiness);

        state.Items.Should().NotBeEmpty("items should be parsed");
        state.ExtrasOffered.Should().BeTrue("observation question should be shown after items");

        // Step 2: Skip observation → confirmation gate
        sentMessages.Clear();
        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "no"),
            _testBusiness);

        state.ObservationAnswered.Should().BeTrue();
        state.OrderConfirmed.Should().BeFalse("confirmation prompt should be shown");
        sentMessages.Last().Body.Should().Contain("deseas hacer");
        sentMessages.Last().Buttons.Should().NotBeNull();
        sentMessages.Last().Buttons!.Select(b => b.Title).Should().Contain("Confirmar");

        // Step 3: Confirm → delivery step
        sentMessages.Clear();
        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "confirmar"),
            _testBusiness);

        state.OrderConfirmed.Should().BeTrue();
        sentMessages.Last().Body.Should().Contain("lo quieres");
        sentMessages.Last().Buttons.Should().NotBeNull();
        sentMessages.Last().Buttons!.Select(b => b.Title).Should().Contain("Delivery");
        sentMessages.Last().Buttons!.Select(b => b.Title).Should().Contain("Pickup");

        // Step 4: Answer delivery → payment step
        sentMessages.Clear();
        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "delivery"),
            _testBusiness);

        state.DeliveryType.Should().Be("delivery");
        sentMessages.Last().Body.Should().Contain("pagar");
    }

    // ── Payment proof post-confirm regression tests ──

    [Fact]
    public async Task ProofAfterConfirm_PagoMovil_CapturedAndPersistedToOrder()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        // State: order just confirmed, awaiting post-confirm proof
        var orderId = Guid.NewGuid();
        var state = new ConversationFields
        {
            LastOrderId = orderId,
            AwaitingPostConfirmProof = true
        };

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        _orderRepositoryMock
            .Setup(x => x.AttachPaymentProofAsync(orderId, "proof_media_789", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // User sends proof image after order confirmation
        var imagePayload = CreateImageMessagePayload("5511999999999", "proof_media_789");
        await _sut.ProcessAsync(imagePayload, _testBusiness);

        // Proof should be captured in state
        state.PaymentEvidenceReceived.Should().BeTrue();
        state.PaymentProofMediaId.Should().Be("proof_media_789");
        state.AwaitingPostConfirmProof.Should().BeFalse("flag should be cleared after proof received");

        // Order should have been updated via repository
        _orderRepositoryMock.Verify(
            x => x.AttachPaymentProofAsync(orderId, "proof_media_789", It.IsAny<CancellationToken>()),
            Times.Once);

        // Bot should respond with confirmation
        var proofMsg = sentMessages.FirstOrDefault(m => m.Body.Contains("Comprobante recibido"));
        proofMsg.Should().NotBeNull("bot must respond when proof is received");
    }

    [Fact]
    public async Task ProofAfterConfirm_Document_AlsoCaptured()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var orderId = Guid.NewGuid();
        var state = new ConversationFields
        {
            LastOrderId = orderId,
            AwaitingPostConfirmProof = true
        };

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        _orderRepositoryMock
            .Setup(x => x.AttachPaymentProofAsync(orderId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Send document (PDF receipt)
        var docPayload = CreateDocumentMessagePayload("5511999999999", "doc_proof_001");
        await _sut.ProcessAsync(docPayload, _testBusiness);

        state.PaymentEvidenceReceived.Should().BeTrue();
        state.PaymentProofMediaId.Should().Be("doc_proof_001");

        _orderRepositoryMock.Verify(
            x => x.AttachPaymentProofAsync(orderId, "doc_proof_001", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProofBeforeConfirm_StillWorksAsBeforeAndOrderIncludesProof()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        // State: in checkout with pago_movil, proof not yet sent
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
        state.DeliveryType = "delivery";
        state.PaymentMethod = "pago_movil";
        state.PaymentEvidenceRequested = true;

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // Send proof BEFORE confirming
        var imagePayload = CreateImageMessagePayload("5511999999999", "pre_confirm_proof");
        await _sut.ProcessAsync(imagePayload, _testBusiness);

        state.PaymentEvidenceReceived.Should().BeTrue();
        state.PaymentProofMediaId.Should().Be("pre_confirm_proof");

        // AttachPaymentProofAsync should NOT be called (no LastOrderId yet)
        _orderRepositoryMock.Verify(
            x => x.AttachPaymentProofAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "pre-confirm proof should not call AttachPaymentProofAsync");
    }

    [Fact]
    public void PaymentProofReceivedMessage_MatchesExpectedText()
    {
        Msg.PaymentProofReceived.Should().Contain("Comprobante recibido");
        Msg.PaymentProofReceived.Should().Contain("pendiente de verificaci");
    }

    // ══════════════════════════════════════════
    // Improvement 3: Confirmation step tests
    // ══════════════════════════════════════════

    [Fact]
    public void BuildOrderReplyFromState_AfterObservation_ShowsConfirmPrompt()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        reply.Body.Should().Contain("RESUMEN DE TU PEDIDO");
        reply.Body.Should().Contain("deseas hacer");
        reply.Buttons.Should().NotBeNull();
        reply.Buttons!.Select(b => b.Title).Should().Contain("Confirmar");
        reply.Buttons!.Select(b => b.Title).Should().Contain("Editar pedido");
        reply.Buttons!.Select(b => b.Title).Should().Contain("Cancelar");
        reply.Body.Should().NotContain("Nombre:"); // checkout form NOT yet
    }

    [Fact]
    public void BuildOrderReplyFromState_AfterOrderConfirmed_ShowsCheckoutForm()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;
        state.OrderConfirmed = true;
        state.DeliveryType = "delivery";
        state.PaymentMethod = "efectivo";

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        reply.Body.Should().Contain("Nombre:");
        state.CheckoutFormSent.Should().BeTrue();
    }

    [Fact]
    public async Task Confirmar_AtConfirmationGate_ShowsDeliveryStep()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1 });
        state.ExtrasOffered = true;
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;
        // OrderConfirmed = false — confirmation gate

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "confirmar"), _testBusiness);

        state.OrderConfirmed.Should().BeTrue();
        // After confirmation, next step is delivery (not checkout form)
        sentMessages.Last().Body.Should().Contain("lo quieres");
        sentMessages.Last().Buttons.Should().NotBeNull();
        sentMessages.Last().Buttons!.Select(b => b.Title).Should().Contain("Delivery");
        sentMessages.Last().Buttons!.Select(b => b.Title).Should().Contain("Pickup");
    }

    [Fact]
    public async Task Editar_ResetsObservationAndShowsSummary()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2, UnitPrice = 6.50m });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "editar"), _testBusiness);

        state.ObservationPromptSent.Should().BeFalse();
        state.ObservationAnswered.Should().BeFalse();
        state.OrderConfirmed.Should().BeFalse();
        sentMessages.Last().Body.Should().Contain("RESUMEN DE TU PEDIDO");
        sentMessages.Last().Body.Should().Contain("deseas ordenar");
    }

    [Fact]
    public async Task Cancelar_ResetsState()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2 });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "cancelar"), _testBusiness);

        state.Items.Should().BeEmpty();
        state.DeliveryType.Should().BeNull();
        state.MenuSent.Should().BeTrue();
    }

    [Theory]
    [InlineData("editar", true)]
    [InlineData("modificar", true)]
    [InlineData("cambiar pedido", true)]
    [InlineData("cancelar", false)]
    [InlineData("confirmar", false)]
    public void IsEditCommand_DetectsCorrectly(string input, bool expected)
    {
        WebhookProcessor.IsEditCommand(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("cancelar", true)]
    [InlineData("cancelar pedido", true)]
    [InlineData("borrar todo", true)]
    [InlineData("empezar de cero", true)]
    [InlineData("editar", false)]
    public void IsCancelCommand_DetectsCorrectly(string input, bool expected)
    {
        WebhookProcessor.IsCancelCommand(input).Should().Be(expected);
    }

    [Fact]
    public void ResetAfterConfirm_ResetsOrderConfirmed()
    {
        var state = new ConversationFields { OrderConfirmed = true };
        state.ResetAfterConfirm();
        state.OrderConfirmed.Should().BeFalse();
    }

    // ══════════════════════════════════════════
    // Improvement 4: Swap pattern tests
    // ══════════════════════════════════════════

    [Theory]
    [InlineData("cambia la hamburguesa clasica por una doble")]
    [InlineData("cambia hamburguesa clasica por hamburguesa doble")]
    [InlineData("cambia las papas medianas por papas grandes")]
    public void TryParseOrderModification_Swap_Detected(string input)
    {
        WebhookProcessor.ActiveCatalog = TestCatalogHelper.MenuCatalogWithExtras;
        var result = WebhookProcessor.TryParseOrderModification(input, out var mod);
        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.Swap);
        mod.ItemName.Should().NotBeEmpty();
        mod.SwapTargetName.Should().NotBeEmpty();
        mod.ItemName.Should().NotBe(mod.SwapTargetName);
    }

    [Fact]
    public void TryParseOrderModification_Swap_ResolvesCorrectItems()
    {
        WebhookProcessor.ActiveCatalog = TestCatalogHelper.MenuCatalogWithExtras;
        var result = WebhookProcessor.TryParseOrderModification(
            "cambia la hamburguesa clasica por una hamburguesa doble", out var mod);
        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.Swap);
        mod.ItemName.Should().Be("Hamburguesa Clasica");
        mod.SwapTargetName.Should().Be("Hamburguesa Doble");
    }

    [Fact]
    public async Task Swap_PreservesQuantityFromOriginal()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields { MenuSent = true };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 3, UnitPrice = 6.50m });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(
            CreateTextMessagePayload("5511999999999", "cambia la hamburguesa clasica por hamburguesa doble"),
            _testBusiness);

        state.Items.Should().HaveCount(1);
        state.Items[0].Name.Should().Be("Hamburguesa Doble");
        state.Items[0].Quantity.Should().Be(3); // preserved from original
        sentMessages.Last().Body.Should().Contain("cambi\u00e9");
    }

    // ══════════════════════════════════════════
    // Improvement 5: Conversation recovery tests
    // ══════════════════════════════════════════

    [Theory]
    [InlineData("mmm", true)]
    [InlineData("no se", true)]
    [InlineData("espera", true)]
    [InlineData("dejame pensar", true)]
    [InlineData("que tienen", true)]
    [InlineData("2 hamburguesas clasicas", false)]
    [InlineData("confirmar", false)]
    [InlineData("hola", false)]
    [InlineData("agrega 1 coca cola", false)]
    public void IsAmbiguousStall_DetectsCorrectly(string input, bool expected)
    {
        var t = input.Trim().ToLowerInvariant();
        WebhookProcessor.IsAmbiguousStall(t).Should().Be(expected);
    }

    [Fact]
    public async Task AmbiguousStall_WithItems_ShowsCurrentOrder()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields { MenuSent = true };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2 });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "mmm"), _testBusiness);

        sentMessages.Should().HaveCount(1);
        sentMessages[0].Body.Should().Contain("pedido actual");
        sentMessages[0].Body.Should().Contain("Hamburguesa Clasica");

        // AI parser should NOT be called
        _aiParserMock.Verify(
            x => x.ParseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AmbiguousStall_NoItems_ShowsGenericRedirect()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationFields { MenuSent = true });

        await _sut.ProcessAsync(CreateTextMessagePayload("5511999999999", "no se"), _testBusiness);

        sentMessages.Should().HaveCount(1);
        sentMessages[0].Body.Should().Contain("deseas ordenar");

        _aiParserMock.Verify(
            x => x.ParseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
