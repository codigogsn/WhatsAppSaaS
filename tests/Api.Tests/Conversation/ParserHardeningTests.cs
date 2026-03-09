using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.DTOs;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Application.Services;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Api.Tests.Conversation;

/// <summary>
/// Targeted tests for parser hardening:
/// - Greeting detection (Venezuelan-style)
/// - Modifier/extra application to existing items
/// - Correction/edit intent ("falto", "no agregaste")
/// - Slang filler word stripping
/// - Word numbers in modification commands
/// </summary>
public class ParserHardeningTests
{
    private readonly Mock<IAiParser> _aiParserMock;
    private readonly Mock<IWhatsAppClient> _whatsAppClientMock;
    private readonly Mock<IOrderRepository> _orderRepositoryMock;
    private readonly Mock<IConversationStateStore> _stateStoreMock;
    private readonly WebhookProcessor _sut;
    private readonly BusinessContext _testBusiness;

    private static int _msgCounter;

    public ParserHardeningTests()
    {
        _aiParserMock = new Mock<IAiParser>();
        _whatsAppClientMock = new Mock<IWhatsAppClient>();
        _orderRepositoryMock = new Mock<IOrderRepository>();
        _stateStoreMock = new Mock<IConversationStateStore>();

        _orderRepositoryMock
            .Setup(x => x.AddOrderAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

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

        // Set active catalog for static helper access (include extras for modifier tests)
        WebhookProcessor.ActiveCatalog = TestCatalogHelper.MenuCatalogWithExtras;
    }

    // ═══════════════════════════════════════════════════════════
    //   A) GREETING DETECTION — Venezuelan greetings
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("epa bro")]
    [InlineData("epale")]
    [InlineData("buenas")]
    [InlineData("que tal")]
    [InlineData("epa")]
    [InlineData("epa mano")]
    [InlineData("buenas tardes")]
    [InlineData("buenas noches")]
    [InlineData("que hubo")]
    [InlineData("habla")]
    [InlineData("hablame")]
    [InlineData("hola bro")]
    public void Greeting_VenezuelanStyle_Detected(string input)
    {
        var t = WebhookProcessor.Normalize(input);
        WebhookProcessor.IsGreeting(t).Should().BeTrue(
            $"\"{input}\" should be recognized as a greeting");
    }

    [Fact]
    public async Task Greeting_EpaBro_TriggersWelcomeFlow()
    {
        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var payload = MakePayload("epa bro");
        await _sut.ProcessAsync(payload, _testBusiness);

        // Should trigger greeting sequence: menu sent flag set
        state.MenuSent.Should().BeTrue("epa bro should trigger full welcome/menu flow");
    }

    [Fact]
    public async Task Greeting_Epale_TriggersWelcomeFlow()
    {
        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var payload = MakePayload("epale");
        await _sut.ProcessAsync(payload, _testBusiness);

        state.MenuSent.Should().BeTrue("epale should trigger full welcome/menu flow");
    }

    [Fact]
    public async Task Greeting_BuenasTardes_TriggersWelcomeFlow()
    {
        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var payload = MakePayload("buenas tardes");
        await _sut.ProcessAsync(payload, _testBusiness);

        state.MenuSent.Should().BeTrue("buenas tardes should trigger full welcome/menu flow");
    }

    [Fact]
    public async Task Greeting_QueTal_TriggersWelcomeFlow()
    {
        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var payload = MakePayload("que tal");
        await _sut.ProcessAsync(payload, _testBusiness);

        state.MenuSent.Should().BeTrue("que tal should trigger full welcome/menu flow");
    }

    // ═══════════════════════════════════════════════════════════
    //   B) MODIFIER / EXTRA APPLICATION
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ModifierParse_ExtraQuesoEnLasHamburguesas_Detected()
    {
        var result = WebhookProcessor.TryParseOrderModification(
            "extra queso en las 2 hamburguesas", out var mod);

        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.AddModifier);
        mod.ModifierText.Should().Be("extra queso");
        mod.TargetItemName.Should().Be("Hamburguesa Clasica");
        mod.Quantity.Should().Be(2);
    }

    [Fact]
    public void ModifierParse_PonleExtraQuesoALasHamburguesas_Detected()
    {
        var result = WebhookProcessor.TryParseOrderModification(
            "ponle extra queso a las hamburguesas", out var mod);

        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.AddModifier);
        mod.ModifierText.Should().Be("extra queso");
        mod.TargetItemName.Should().Be("Hamburguesa Clasica");
    }

    [Fact]
    public void ModifierParse_AgregaExtraQuesoALas2Hamburguesas_Detected()
    {
        var result = WebhookProcessor.TryParseOrderModification(
            "agrega extra queso a las 2 hamburguesas", out var mod);

        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.AddModifier);
        mod.ModifierText.Should().Be("extra queso");
        mod.TargetItemName.Should().Be("Hamburguesa Clasica");
    }

    [Fact]
    public void ModifierParse_SinCebollaEnLaHamburguesa_Detected()
    {
        var result = WebhookProcessor.TryParseOrderModification(
            "sin cebolla en la hamburguesa", out var mod);

        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.AddModifier);
        mod.ModifierText.Should().Be("sin cebolla");
        mod.TargetItemName.Should().Be("Hamburguesa Clasica");
    }

    [Fact]
    public async Task Modifier_SinCebollaEnHamburguesas_AppliedCorrectly()
    {
        // Setup: order already has 2 hamburguesas clasicas
        var state = new ConversationFields
        {
            MenuSent = true,
            Items = new List<ConversationItemEntry>
            {
                new() { Name = "Hamburguesa Clasica", Quantity = 2, UnitPrice = 6.50m }
            },
            DeliveryType = "delivery",
            ObservationAnswered = true
        };
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var payload = MakePayload("sin cebolla en las 2 hamburguesas");
        await _sut.ProcessAsync(payload, _testBusiness);

        // Modifier should be applied to the hamburguesas
        var hamburguesas = state.Items.FirstOrDefault(i => i.Name == "Hamburguesa Clasica");
        hamburguesas.Should().NotBeNull();
        hamburguesas!.Modifiers.Should().Contain("sin cebolla");
        // "sin" modifiers have $0 price impact
        hamburguesas.UnitPrice.Should().Be(6.50m, "sin modifier should not change price");
        // Should NOT create a separate item
        state.Items.Should().HaveCount(1, "modifier should not create new item");
    }

    [Fact]
    public void ModifierParse_ExtraQuesoNotMappedToMalta()
    {
        // "extra queso" as a standalone modification should NOT resolve as Malta via fuzzy matching
        var result = WebhookProcessor.TryParseOrderModification(
            "extra queso en las hamburguesas", out var mod);

        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.AddModifier);
        mod.ModifierText.Should().Be("extra queso");
        // Target should be hamburguesa, NOT mapped to any beverage
        mod.TargetItemName.Should().NotBe("Malta");
    }

    // ── Inline modifiers in order text ──

    [Fact]
    public void ParseOrder_HamburguesaDoblConExtraQueso_ModifierAttached()
    {
        var parsed = WebhookProcessor.ParseOrderText("1 hamburguesa doble con extra queso");

        parsed.Should().ContainSingle();
        parsed[0].Name.Should().Be("Hamburguesa Doble");
        parsed[0].Modifiers.Should().Contain("extra queso");
    }

    [Fact]
    public void ParseOrder_2HamburguesasSinCebolla_ModifierAttached()
    {
        var parsed = WebhookProcessor.ParseOrderText("2 hamburguesas sin cebolla");

        parsed.Should().ContainSingle();
        parsed[0].Name.Should().Be("Hamburguesa Clasica");
        parsed[0].Quantity.Should().Be(2);
        parsed[0].Modifiers.Should().Contain("sin cebolla");
    }

    [Fact]
    public void ParseOrder_1PerroConExtraQueso_ModifierAttached()
    {
        var parsed = WebhookProcessor.ParseOrderText("1 perro con extra queso");

        parsed.Should().ContainSingle();
        parsed[0].Name.Should().Be("Perro Clasico");
        parsed[0].Modifiers.Should().Contain("extra queso");
    }

    // ═══════════════════════════════════════════════════════════
    //   C) CORRECTION / EDIT INTENT
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("falto el extra de queso")]
    [InlineData("falta el extra de queso")]
    [InlineData("te falto el extra de queso")]
    [InlineData("no agregaste el extra de queso")]
    [InlineData("no pusiste el extra queso")]
    public void CorrectionIntent_Detected(string input)
    {
        var result = WebhookProcessor.TryParseOrderModification(input, out var mod);

        result.Should().BeTrue($"\"{input}\" should be detected as correction intent");
        mod.Type.Should().Be(WebhookProcessor.ModificationType.AddModifier);
        mod.ModifierText.Should().Contain("extra queso");
    }

    [Fact]
    public async Task Correction_FaltoExtraQueso_AppliedToExistingItems()
    {
        // Setup: order has 2 hamburguesas
        var state = new ConversationFields
        {
            MenuSent = true,
            Items = new List<ConversationItemEntry>
            {
                new() { Name = "Hamburguesa Clasica", Quantity = 2, UnitPrice = 6.50m }
            },
            DeliveryType = "delivery",
            ObservationAnswered = true
        };
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var payload = MakePayload("falto el extra de queso");
        await _sut.ProcessAsync(payload, _testBusiness);

        // Should add modifier to existing items, NOT create "Malta" or random product
        state.Items.Should().HaveCount(1, "correction should not add new product line");
        state.Items[0].Modifiers.Should().Contain("extra queso");
        state.Items.Should().NotContain(i => i.Name == "Malta");
    }

    [Fact]
    public async Task Correction_NoAgregasteSinCebolla_AppliedToExistingItems()
    {
        var state = new ConversationFields
        {
            MenuSent = true,
            Items = new List<ConversationItemEntry>
            {
                new() { Name = "Hamburguesa Clasica", Quantity = 2, UnitPrice = 6.50m }
            },
            DeliveryType = "delivery",
            ObservationAnswered = true
        };
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var payload = MakePayload("sin cebolla en las hamburguesas");
        await _sut.ProcessAsync(payload, _testBusiness);

        state.Items.Should().HaveCount(1);
        state.Items[0].Modifiers.Should().Contain("sin cebolla");
        state.Items[0].UnitPrice.Should().Be(6.50m, "sin modifier should not change price");
    }

    // ═══════════════════════════════════════════════════════════
    //   D) SLANG / NOISE STRIPPING
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("bro dame 2 hamburguesas", "Hamburguesa Clasica", 2)]
    [InlineData("pana dame 1 coca cola", "Coca Cola", 1)]
    [InlineData("vale dame 1 perro caliente", "Perro Clasico", 1)]
    [InlineData("mano 2 hamburguesas dobles", "Hamburguesa Doble", 2)]
    [InlineData("loco dame 3 cocas", "Coca Cola", 3)]
    [InlineData("hermano dame 1 hamburguesa", "Hamburguesa Clasica", 1)]
    public void SlangNoise_StrippedCorrectly(string input, string expectedItem, int expectedQty)
    {
        WebhookProcessor.TryParseQuickOrder(input, out var items, out _, out _);

        items.Should().Contain(i => i.Name == expectedItem && i.Quantity == expectedQty,
            $"parser should find {expectedQty}x {expectedItem} in \"{input}\"");
    }

    [Fact]
    public void SlangNoise_EpaBroDame2Hamburguesas_FullParse()
    {
        // The original failing Case 1 from the stress tests
        WebhookProcessor.TryParseQuickOrder(
            "epa bro dame 2 hamburguesas clasicas y 1 coca cola",
            out var items, out _, out _);

        items.Should().HaveCount(2);
        items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    // ═══════════════════════════════════════════════════════════
    //   E) WORD NUMBERS IN MODIFICATION COMMANDS
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void WordNumber_AgregaUnaCoca_Detected()
    {
        var result = WebhookProcessor.TryParseOrderModification("agrega una coca", out var mod);

        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.Add);
        mod.Quantity.Should().Be(1);
        mod.ItemName.Should().Be("Coca Cola");
    }

    [Fact]
    public void WordNumber_AgregaDosPapasFritas_Detected()
    {
        var result = WebhookProcessor.TryParseOrderModification("agrega dos papas fritas", out var mod);

        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.Add);
        mod.Quantity.Should().Be(2);
        mod.ItemName.Should().Be("Papas Medianas");
    }

    [Theory]
    [InlineData("agrega una hamburguesa", 1, "Hamburguesa Clasica")]
    [InlineData("agrega tres cocas", 3, "Coca Cola")]
    [InlineData("ponme una pepsi", 1, "Pepsi")]
    [InlineData("sumale dos perros", 2, "Perro Clasico")]
    [InlineData("agregame un agua", 1, "Agua")]
    public void WordNumber_VariousModifications(string input, int expectedQty, string expectedItem)
    {
        var result = WebhookProcessor.TryParseOrderModification(input, out var mod);

        result.Should().BeTrue($"\"{input}\" should be a valid modification");
        mod.Type.Should().Be(WebhookProcessor.ModificationType.Add);
        mod.Quantity.Should().Be(expectedQty);
        mod.ItemName.Should().Be(expectedItem);
    }

    // ═══════════════════════════════════════════════════════════
    //   F) PONLE verb support
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Ponle_ExtraQuesoALaHamburguesa()
    {
        var result = WebhookProcessor.TryParseOrderModification(
            "ponle extra queso a la hamburguesa", out var mod);

        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.AddModifier);
        mod.ModifierText.Should().Be("extra queso");
    }

    [Fact]
    public void Agregale_ExtraTocinetaALaDoble()
    {
        var result = WebhookProcessor.TryParseOrderModification(
            "agregale extra tocineta a la doble", out var mod);

        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.AddModifier);
        mod.ModifierText.Should().Be("extra tocineta");
        mod.TargetItemName.Should().Be("Hamburguesa Doble");
    }

    // ═══════════════════════════════════════════════════════════
    //   G) START-FLOW IDEMPOTENCY
    //   Continuation intents after greeting must NOT resend menu
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task HolaQueTal_ThenQuisieraHacerUnPedido_SendsContinuationPrompt()
    {
        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // Message 1: greeting — full welcome/menu (3 messages)
        await _sut.ProcessAsync(MakePayload("hola que tal"), _testBusiness);
        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3),
            "first greeting should send 3 messages (welcome + menu + prompt)");
        _whatsAppClientMock.Invocations.Clear();

        // Message 2: ordering intent — continuation prompt only
        await _sut.ProcessAsync(MakePayload("quisiera hacer un pedido"), _testBusiness);

        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "ordering intent after recent greeting should send only 1 message");
        var sentBody = (OutgoingMessage)_whatsAppClientMock.Invocations
            .First(i => i.Method.Name == "SendTextMessageAsync").Arguments[0];
        sentBody.Body.Should().StartWith("Perfecto",
            "continuation prompt should use natural language, not repeat the menu prompt");
        sentBody.Body.Should().NotContain("\u00bfQu\u00e9 deseas ordenar?",
            "must NOT repeat the initial order prompt");
    }

    [Fact]
    public async Task Hola_ThenQuieroPedir_SendsContinuationPrompt()
    {
        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(MakePayload("hola"), _testBusiness);
        _whatsAppClientMock.Invocations.Clear();

        await _sut.ProcessAsync(MakePayload("quiero pedir"), _testBusiness);

        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.Once);
        var sentBody = (OutgoingMessage)_whatsAppClientMock.Invocations
            .First(i => i.Method.Name == "SendTextMessageAsync").Arguments[0];
        sentBody.Body.Should().StartWith("Perfecto");
        sentBody.Body.Should().NotContain("\u00bfQu\u00e9 deseas ordenar?");
    }

    [Fact]
    public async Task Buenas_ThenVoyAPedir_SendsContinuationPrompt()
    {
        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(MakePayload("buenas"), _testBusiness);
        _whatsAppClientMock.Invocations.Clear();

        await _sut.ProcessAsync(MakePayload("voy a pedir"), _testBusiness);

        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.Once);
        var sentBody = (OutgoingMessage)_whatsAppClientMock.Invocations
            .First(i => i.Method.Name == "SendTextMessageAsync").Arguments[0];
        sentBody.Body.Should().StartWith("Perfecto");
        sentBody.Body.Should().NotContain("\u00bfQu\u00e9 deseas ordenar?");
    }

    [Fact]
    public async Task NewConversation_StillShowsFullGreeting()
    {
        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(MakePayload("quisiera hacer un pedido"), _testBusiness);

        // Fresh session — full greeting should fire, not the continuation prompt
        _whatsAppClientMock.Verify(
            x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3),
            "first message in fresh conversation should trigger full greeting sequence");
        state.MenuSent.Should().BeTrue();
        // The 3rd message should be the PDF menu prompt, not the continuation
        var thirdMsg = (OutgoingMessage)_whatsAppClientMock.Invocations
            .Where(i => i.Method.Name == "SendTextMessageAsync")
            .ElementAt(2).Arguments[0];
        thirdMsg.Body.Should().Contain("Envíame tu pedido",
            "fresh conversation prompt should use the PDF menu prompt");
    }

    // ═══════════════════════════════════════════════════════════
    //   H) MIXED ORDER + MODIFIER — Bug 2
    //   "2 hamburguesas con bbq" = 2x Hamburguesa BBQ, not split
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ParseOrder_2HamburguesasConBbq_ResolvesAsBbqVariant()
    {
        var parsed = WebhookProcessor.ParseOrderText("2 hamburguesas con bbq");

        parsed.Should().ContainSingle("should be a single item, not split");
        parsed[0].Name.Should().Be("Hamburguesa BBQ");
        parsed[0].Quantity.Should().Be(2);
    }

    [Fact]
    public void ParseOrder_HamburguesaConBbq_ResolvesAsBbqVariant()
    {
        var parsed = WebhookProcessor.ParseOrderText("1 hamburguesa con bbq");

        parsed.Should().ContainSingle();
        parsed[0].Name.Should().Be("Hamburguesa BBQ");
        parsed[0].Quantity.Should().Be(1);
    }

    [Fact]
    public void TrySplitOnConMenuItem_HamburguesasConBbq_DoesNotSplit()
    {
        var result = WebhookProcessor.SplitIntoOrderSegments("2 hamburguesas con bbq");

        // Should stay as one segment, not split into "2 hamburguesas" + "bbq"
        result.Should().ContainSingle("'hamburguesas con bbq' is Hamburguesa BBQ — should not split");
    }

    [Fact]
    public void ParseOrder_HamburguesasConPapas_StillSplits()
    {
        // "con papas" IS a separate item — should still split
        var parsed = WebhookProcessor.ParseOrderText("2 hamburguesas con papas");

        parsed.Should().HaveCount(2);
        parsed.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        parsed.Should().Contain(i => i.Name == "Papas Medianas");
    }

    [Fact]
    public void ParseOrder_PerroConQueso_ResolvesAsPerroConQueso()
    {
        // "Perro con Queso" is a menu item — should NOT split
        var parsed = WebhookProcessor.ParseOrderText("1 perro con queso");

        parsed.Should().ContainSingle();
        parsed[0].Name.Should().Be("Perro con Queso");
    }

    [Fact]
    public void ParseOrder_PapasConQueso_ResolvesAsPapasConQueso()
    {
        var parsed = WebhookProcessor.ParseOrderText("1 papas con queso");

        parsed.Should().ContainSingle();
        parsed[0].Name.Should().Be("Papas con Queso");
    }

    // ═══════════════════════════════════════════════════════════
    //   I) GUIDED ORDERING FLOW — cashier-style steps
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task GuidedFlow_ObservationYes_ShowsFormatInstructions()
    {
        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        // Step 1: Order items → observation question
        await _sut.ProcessAsync(MakePayload("1 hamburguesa"), _testBusiness);
        state.Items.Should().NotBeEmpty();
        state.ExtrasOffered.Should().BeTrue();
        sentMessages.Last().Body.Should().Contain("observaci\u00f3n");

        // Step 2: Say YES → observation format instructions
        sentMessages.Clear();
        await _sut.ProcessAsync(MakePayload("s\u00ed"), _testBusiness);

        state.ObservationPromptSent.Should().BeTrue("'s\u00ed' should trigger observation format");
        sentMessages.Last().Body.Should().Contain("sin cebolla",
            "should show observation format examples");
    }

    [Fact]
    public async Task GuidedFlow_ObservationNo_SkipsToConfirmation()
    {
        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        // Order → observation question → NO → confirmation
        await _sut.ProcessAsync(MakePayload("1 hamburguesa"), _testBusiness);
        sentMessages.Clear();
        await _sut.ProcessAsync(MakePayload("no"), _testBusiness);

        state.ObservationAnswered.Should().BeTrue();
        sentMessages.Last().Body.Should().Contain("RESUMEN");
        sentMessages.Last().Body.Should().Contain("deseas hacer");
        sentMessages.Last().Buttons.Should().NotBeNull();
        sentMessages.Last().Buttons!.Select(b => b.Title).Should().Contain("Confirmar");
    }

    [Fact]
    public async Task GuidedFlow_ObservationTextParsed_ThenConfirmation()
    {
        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        // Order → YES → send observation → confirmation
        await _sut.ProcessAsync(MakePayload("1 hamburguesa bbq"), _testBusiness);
        await _sut.ProcessAsync(MakePayload("s\u00ed"), _testBusiness);

        sentMessages.Clear();
        await _sut.ProcessAsync(MakePayload("sin cebolla"), _testBusiness);

        state.ObservationAnswered.Should().BeTrue();
        state.SpecialInstructions.Should().Contain("sin cebolla");
        // After observation, should show confirmation gate
        sentMessages.Last().Body.Should().Contain("deseas hacer");
        sentMessages.Last().Buttons.Should().NotBeNull();
        sentMessages.Last().Buttons!.Select(b => b.Title).Should().Contain("Confirmar");
    }

    [Fact]
    public async Task GuidedFlow_ConfirmThenDelivery()
    {
        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        // Order → NO observation → CONFIRMAR → delivery prompt
        await _sut.ProcessAsync(MakePayload("1 hamburguesa"), _testBusiness);
        await _sut.ProcessAsync(MakePayload("no"), _testBusiness);

        sentMessages.Clear();
        await _sut.ProcessAsync(MakePayload("confirmar"), _testBusiness);

        state.OrderConfirmed.Should().BeTrue();
        sentMessages.Last().Body.Should().Contain("lo quieres");
        sentMessages.Last().Buttons.Should().NotBeNull();
        sentMessages.Last().Buttons!.Select(b => b.Title).Should().Contain("Delivery");
        sentMessages.Last().Buttons!.Select(b => b.Title).Should().Contain("Pickup");
    }

    [Fact]
    public async Task GuidedFlow_DeliveryThenPayment()
    {
        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        // Full flow to payment
        await _sut.ProcessAsync(MakePayload("1 hamburguesa"), _testBusiness);
        await _sut.ProcessAsync(MakePayload("no"), _testBusiness);
        await _sut.ProcessAsync(MakePayload("confirmar"), _testBusiness);

        sentMessages.Clear();
        await _sut.ProcessAsync(MakePayload("delivery"), _testBusiness);

        state.DeliveryType.Should().Be("delivery");
        sentMessages.Last().Body.Should().Contain("pagar");
        sentMessages.Last().Buttons.Should().NotBeNull();
        sentMessages.Last().Buttons!.Select(b => b.Title).Should().Contain("Efectivo");
        sentMessages.Last().Buttons!.Select(b => b.Title).Should().Contain("Zelle");
    }

    [Fact]
    public async Task GuidedFlow_PaymentThenCheckout()
    {
        var state = new ConversationFields();
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        // Full flow to checkout form
        await _sut.ProcessAsync(MakePayload("1 hamburguesa"), _testBusiness);
        await _sut.ProcessAsync(MakePayload("no"), _testBusiness);
        await _sut.ProcessAsync(MakePayload("confirmar"), _testBusiness);
        await _sut.ProcessAsync(MakePayload("pickup"), _testBusiness);

        sentMessages.Clear();
        await _sut.ProcessAsync(MakePayload("efectivo"), _testBusiness);

        state.PaymentMethod.Should().Be("efectivo");
        state.CheckoutFormSent.Should().BeTrue();
        sentMessages.Last().Body.Should().Contain("Nombre:");
    }

    // ═══════════════════════════════════════════════════════════
    //   Helpers
    // ═══════════════════════════════════════════════════════════

    private static WebhookPayload MakePayload(string body) => new()
    {
        Entry =
        [
            new WebhookEntry
            {
                Changes =
                [
                    new WebhookChange
                    {
                        Value = new WebhookChangeValue
                        {
                            Metadata = new WebhookMetadata
                            {
                                PhoneNumberId = "123456789"
                            },
                            Messages =
                            [
                                new WebhookMessage
                                {
                                    Id = $"wamid.test-{Interlocked.Increment(ref _msgCounter)}",
                                    From = "5511999999999",
                                    Type = "text",
                                    Timestamp = "1234567890",
                                    Text = new WebhookText { Body = body }
                                }
                            ]
                        }
                    }
                ]
            }
        ]
    };

    // ── GOAL 2: No "delivery" in order examples ──

    [Fact]
    public void WhatToOrder_DoesNotContainDelivery()
    {
        Msg.WhatToOrder.Should().NotContain("delivery", because: "delivery/pickup is asked in a separate step");
    }

    [Fact]
    public void ContinueOrder_DoesNotContainDelivery()
    {
        Msg.ContinueOrder.Should().NotContain("delivery", because: "delivery/pickup is asked in a separate step");
    }

    [Fact]
    public void MenuPrompt_DoesNotContainDelivery()
    {
        Msg.MenuPrompt.Should().NotContain("delivery", because: "delivery/pickup is asked in a separate step");
    }

    // ── GOAL 1: Real reply buttons are sent ──

    [Fact]
    public void ObservationStep_HasReplyButtons()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        reply.Body.Should().Contain("observaci\u00f3n");
        reply.Buttons.Should().NotBeNull();
        reply.Buttons!.Should().HaveCount(2);
        reply.Buttons!.Select(b => b.Title).Should().Contain("S\u00ed");
        reply.Buttons!.Select(b => b.Title).Should().Contain("No");
    }

    [Fact]
    public void ConfirmationStep_HasReplyButtons()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        reply.Body.Should().Contain("RESUMEN DE TU PEDIDO");
        reply.Buttons.Should().NotBeNull();
        reply.Buttons!.Should().HaveCount(3);
        reply.Buttons!.Select(b => b.Title).Should().Contain("Confirmar");
        reply.Buttons!.Select(b => b.Title).Should().Contain("Editar pedido");
        reply.Buttons!.Select(b => b.Title).Should().Contain("Cancelar");
    }

    [Fact]
    public void DeliveryStep_HasReplyButtons()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        state.OrderConfirmed = true;

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        reply.Body.Should().Contain("quieres");
        reply.Buttons.Should().NotBeNull();
        reply.Buttons!.Should().HaveCount(2);
        reply.Buttons!.Select(b => b.Title).Should().Contain("Delivery");
        reply.Buttons!.Select(b => b.Title).Should().Contain("Pickup");
    }

    [Fact]
    public void PaymentStep_HasReplyButtons()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        state.OrderConfirmed = true;
        state.DeliveryType = "delivery";

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        reply.Body.Should().Contain("pagar");
        reply.Buttons.Should().NotBeNull();
        reply.Buttons!.Should().HaveCount(3);
        reply.Buttons!.Select(b => b.Title).Should().Contain("Efectivo");
        reply.Buttons!.Select(b => b.Title).Should().Contain("Pago móvil");
        reply.Buttons!.Select(b => b.Title).Should().Contain("Zelle");
    }

    // ═══════════════════════════════════════════════════════════
    //   INTERACTIVE BUTTON REPLIES — end-to-end flow tests
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void MapButtonIdToText_AllButtonsMapCorrectly()
    {
        WebhookProcessor.MapButtonIdToText("btn_obs_si").Should().Be("si");
        WebhookProcessor.MapButtonIdToText("btn_obs_no").Should().Be("no");
        WebhookProcessor.MapButtonIdToText("btn_confirmar").Should().Be("confirmar");
        WebhookProcessor.MapButtonIdToText("btn_editar").Should().Be("editar");
        WebhookProcessor.MapButtonIdToText("btn_cancelar").Should().Be("cancelar");
        WebhookProcessor.MapButtonIdToText("btn_delivery").Should().Be("delivery");
        WebhookProcessor.MapButtonIdToText("btn_pickup").Should().Be("pickup");
        WebhookProcessor.MapButtonIdToText("btn_efectivo").Should().Be("efectivo");
        WebhookProcessor.MapButtonIdToText("btn_pago_movil").Should().Be("pago movil");
        WebhookProcessor.MapButtonIdToText("btn_zelle").Should().Be("zelle");
        WebhookProcessor.MapButtonIdToText("unknown_btn").Should().BeNull();
    }

    [Fact]
    public async Task ObservationButton_Yes_AdvancesToObservationFormat()
    {
        var state = new ConversationFields { MenuSent = true };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.ExtrasOffered = true; // observation question was sent
        SetupState(state);

        await _sut.ProcessAsync(MakeButtonPayload("btn_obs_si", "S\u00ed"), _testBusiness);

        state.ObservationPromptSent.Should().BeTrue("tapping S\u00ed should advance to observation format");
        _whatsAppClientMock.Verify(x => x.SendTextMessageAsync(
            It.Is<OutgoingMessage>(m => m.Body.Contains("sin cebolla")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ObservationButton_No_AdvancesToConfirmation()
    {
        var state = new ConversationFields { MenuSent = true };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        SetupState(state);

        await _sut.ProcessAsync(MakeButtonPayload("btn_obs_no", "No"), _testBusiness);

        state.ObservationAnswered.Should().BeTrue("tapping No should skip observation");
        _whatsAppClientMock.Verify(x => x.SendTextMessageAsync(
            It.Is<OutgoingMessage>(m => m.Body.Contains("RESUMEN DE TU PEDIDO")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfirmButton_AdvancesToDeliveryStep()
    {
        var state = new ConversationFields { MenuSent = true };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        SetupState(state);

        await _sut.ProcessAsync(MakeButtonPayload("btn_confirmar", "Confirmar"), _testBusiness);

        state.OrderConfirmed.Should().BeTrue("tapping Confirmar should confirm the order");
        _whatsAppClientMock.Verify(x => x.SendTextMessageAsync(
            It.Is<OutgoingMessage>(m => m.Buttons != null && m.Buttons.Any(b => b.Id == "btn_delivery")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EditButton_ReturnsToEditFlow()
    {
        var state = new ConversationFields { MenuSent = true };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        SetupState(state);

        await _sut.ProcessAsync(MakeButtonPayload("btn_editar", "Editar pedido"), _testBusiness);

        state.OrderConfirmed.Should().BeFalse("tapping Editar should reset confirmation");
        state.ExtrasOffered.Should().BeFalse("edit should reset observation flow");
    }

    [Fact]
    public async Task CancelButton_ResetsFlow()
    {
        var state = new ConversationFields { MenuSent = true };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        SetupState(state);

        await _sut.ProcessAsync(MakeButtonPayload("btn_cancelar", "Cancelar"), _testBusiness);

        state.Items.Should().BeEmpty("tapping Cancelar should clear the order");
    }

    [Fact]
    public async Task DeliveryButton_AdvancesToPayment()
    {
        var state = new ConversationFields { MenuSent = true };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        state.OrderConfirmed = true;
        SetupState(state);

        await _sut.ProcessAsync(MakeButtonPayload("btn_delivery", "Delivery"), _testBusiness);

        state.DeliveryType.Should().Be("delivery");
        _whatsAppClientMock.Verify(x => x.SendTextMessageAsync(
            It.Is<OutgoingMessage>(m => m.Buttons != null && m.Buttons.Any(b => b.Id == "btn_efectivo")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PickupButton_AdvancesToPayment()
    {
        var state = new ConversationFields { MenuSent = true };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        state.OrderConfirmed = true;
        SetupState(state);

        await _sut.ProcessAsync(MakeButtonPayload("btn_pickup", "Pickup"), _testBusiness);

        state.DeliveryType.Should().Be("pickup");
        _whatsAppClientMock.Verify(x => x.SendTextMessageAsync(
            It.Is<OutgoingMessage>(m => m.Buttons != null && m.Buttons.Any(b => b.Id == "btn_efectivo")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PaymentButton_Efectivo_AdvancesToCheckout()
    {
        var state = new ConversationFields { MenuSent = true };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        state.OrderConfirmed = true;
        state.DeliveryType = "delivery";
        SetupState(state);

        await _sut.ProcessAsync(MakeButtonPayload("btn_efectivo", "Efectivo"), _testBusiness);

        state.PaymentMethod.Should().Be("efectivo");
        state.CheckoutFormSent.Should().BeTrue("should advance to checkout form");
    }

    [Fact]
    public async Task PaymentButton_PagoMovil_AdvancesToCheckout()
    {
        var state = new ConversationFields { MenuSent = true };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        state.OrderConfirmed = true;
        state.DeliveryType = "delivery";
        SetupState(state);

        await _sut.ProcessAsync(MakeButtonPayload("btn_pago_movil", "Pago móvil"), _testBusiness);

        state.PaymentMethod.Should().Be("pago_movil");
    }

    [Fact]
    public async Task PaymentButton_Zelle_AdvancesToCheckout()
    {
        var state = new ConversationFields { MenuSent = true };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        state.OrderConfirmed = true;
        state.DeliveryType = "delivery";
        SetupState(state);

        await _sut.ProcessAsync(MakeButtonPayload("btn_zelle", "Zelle"), _testBusiness);

        state.PaymentMethod.Should().Be("zelle");
    }

    // ── Helpers ──

    private void SetupState(ConversationFields state)
    {
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);
    }

    private static WebhookPayload MakeButtonPayload(string buttonId, string buttonTitle) => new()
    {
        Entry =
        [
            new WebhookEntry
            {
                Changes =
                [
                    new WebhookChange
                    {
                        Value = new WebhookChangeValue
                        {
                            Metadata = new WebhookMetadata
                            {
                                PhoneNumberId = "123456789"
                            },
                            Messages =
                            [
                                new WebhookMessage
                                {
                                    Id = $"wamid.btn-{Interlocked.Increment(ref _msgCounter)}",
                                    From = "5511999999999",
                                    Type = "interactive",
                                    Timestamp = "1234567890",
                                    Interactive = new WebhookInteractive
                                    {
                                        Type = "button_reply",
                                        ButtonReply = new WebhookButtonReply
                                        {
                                            Id = buttonId,
                                            Title = buttonTitle
                                        }
                                    }
                                }
                            ]
                        }
                    }
                ]
            }
        ]
    };
}
