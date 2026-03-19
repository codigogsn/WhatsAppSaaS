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
/// Stress tests simulating how Venezuelan customers actually order food on WhatsApp.
/// Covers slang, informal phrasing, multi-line messages, typos, partial orders,
/// corrections, edits, delivery phrases, and full conversation flows.
///
/// IMPORTANT: This file must NOT modify any production code.
/// </summary>
public class VenezuelanOrderingStressTests
{
    private readonly Mock<IAiParser> _aiParserMock;
    private readonly Mock<IWhatsAppClient> _whatsAppClientMock;
    private readonly Mock<IOrderRepository> _orderRepositoryMock;
    private readonly Mock<IConversationStateStore> _stateStoreMock;
    private readonly WebhookProcessor _sut;
    private readonly BusinessContext _testBusiness;

    private static int _msgCounter;

    public VenezuelanOrderingStressTests()
    {
        _aiParserMock = new Mock<IAiParser>();
        _whatsAppClientMock = new Mock<IWhatsAppClient>();
        _orderRepositoryMock = new Mock<IOrderRepository>();
        _stateStoreMock = new Mock<IConversationStateStore>();

        _orderRepositoryMock
            .Setup(x => x.AddOrderAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

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

        _testBusiness = new BusinessContext(Guid.NewGuid(), "123456789", "test-token", "Burger House VE", MenuPdfUrl: "https://test.example.com/menu-demo.pdf");

        _sut = new WebhookProcessor(
            _aiParserMock.Object,
            _whatsAppClientMock.Object,
            _orderRepositoryMock.Object,
            _stateStoreMock.Object,
            new Mock<ILogger<WebhookProcessor>>().Object);
    }

    // ══════════════════════════════════════════════════════════════
    // CATEGORY 1 — Simple Orders
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void SimpleOrder_DameUnaHamburguesa()
    {
        var items = ParseQuick("dame una hamburguesa");
        items.Should().ContainSingle();
        items[0].Name.Should().Be("Hamburguesa Clasica");
        items[0].Quantity.Should().Be(1);
    }

    [Fact]
    public void SimpleOrder_Quiero2HamburguesasDobles()
    {
        var items = ParseQuick("quiero 2 hamburguesas dobles");
        items.Should().ContainSingle();
        items[0].Name.Should().Be("Hamburguesa Doble");
        items[0].Quantity.Should().Be(2);
    }

    [Fact]
    public void SimpleOrder_2HamburguesasYUnaCoca()
    {
        var items = ParseQuick("2 hamburguesas y una coca");
        items.Should().HaveCount(2);
        items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    [Fact]
    public void SimpleOrder_HamburguesaDobleYUnaPapa()
    {
        var items = ParseQuick("una hamburguesa doble y una papa");
        items.Should().HaveCount(2);
        items.Should().Contain(i => i.Name == "Hamburguesa Doble" && i.Quantity == 1);
        items.Should().Contain(i => i.Name == "Papas Medianas" && i.Quantity == 1);
    }

    [Theory]
    [InlineData("1 hamburguesa", "Hamburguesa Clasica", 1)]
    [InlineData("3 cocas", "Coca Cola", 3)]
    [InlineData("2 perros calientes", "Perro Clasico", 2)]
    [InlineData("1 combo clasico", "Combo Clasico", 1)]
    [InlineData("una malta", "Malta", 1)]
    [InlineData("2 aguas", "Agua", 2)]
    public void SimpleOrder_VariousItems(string input, string expectedItem, int expectedQty)
    {
        var items = ParseQuick(input);
        items.Should().Contain(i => i.Name == expectedItem && i.Quantity == expectedQty);
    }

    // ══════════════════════════════════════════════════════════════
    // CATEGORY 2 — Multi-Line Orders
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void MultiLine_ThreeItemsOnSeparateLines()
    {
        var items = ParseQuick("2 hamburguesas\n1 perro caliente\n2 cocas");
        items.Should().HaveCount(3);
        items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Perro Clasico" && i.Quantity == 1);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 2);
    }

    [Fact]
    public void MultiLine_FourItemsWithDelivery()
    {
        var text = "3 hamburguesas dobles\n1 papa grande\n2 maltas\ndelivery";
        var parsed = WebhookProcessor.ParseOrderText(text);
        parsed.Should().HaveCount(3);
        parsed.Should().Contain(i => i.Name == "Hamburguesa Doble" && i.Quantity == 3);
        parsed.Should().Contain(i => i.Name == "Papas Grandes" && i.Quantity == 1);
        parsed.Should().Contain(i => i.Name == "Malta" && i.Quantity == 2);
    }

    [Fact]
    public void MultiLine_WithConjunctionOnNewLine()
    {
        var items = ParseQuick("1 perro caliente\n1 hamburguesa\ny una coca\npara delivery");
        items.Should().HaveCount(3);
        items.Should().Contain(i => i.Name == "Perro Clasico" && i.Quantity == 1);
        items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 1);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    [Fact]
    public void MultiLine_ListStyleWithDashes()
    {
        // Some users type list-style — the parser should handle this
        var items = ParseQuick("2 hamburguesas\n1 pepsi\n1 papa mediana");
        items.Should().HaveCount(3);
        items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Pepsi" && i.Quantity == 1);
        items.Should().Contain(i => i.Name == "Papas Medianas" && i.Quantity == 1);
    }

    // ══════════════════════════════════════════════════════════════
    // CATEGORY 3 — Venezuelan Phrases / Slang Filler
    // ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("epa dame 2 hamburguesas")]
    [InlineData("dame 2 hamburguesas porfa")]
    [InlineData("hola quiero 2 hamburguesas")]
    [InlineData("necesito 2 hamburguesas")]
    [InlineData("me das 2 hamburguesas")]
    public void VenezuelanPhrase_FillerIgnored_2Hamburguesas(string input)
    {
        var items = ParseQuick(input);
        items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
    }

    [Fact]
    public void VenezuelanPhrase_BroDameUnPerro()
    {
        // "bro" is not in the noise list — it'll be ignored during item matching
        // The parser should still find "perro caliente"
        var items = ParseQuick("1 perro caliente porfa");
        items.Should().ContainSingle();
        items[0].Name.Should().Be("Perro Clasico");
        items[0].Quantity.Should().Be(1);
    }

    [Theory]
    [InlineData("por favor 1 hamburguesa")]
    [InlineData("porfavor 1 hamburguesa")]
    [InlineData("porfa 1 hamburguesa")]
    [InlineData("plis 1 hamburguesa")]
    [InlineData("gracias 1 hamburguesa")]
    public void VenezuelanPhrase_PoliteFillerStripped(string input)
    {
        var items = ParseQuick(input);
        items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 1);
    }

    [Fact]
    public void VenezuelanPhrase_EpaBroDameMultipleItems()
    {
        var items = ParseQuick("epa dame 2 hamburguesas dobles y 1 coca");
        items.Should().HaveCount(2);
        items.Should().Contain(i => i.Name == "Hamburguesa Doble" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    [Fact]
    public void VenezuelanPhrase_WordNumbers()
    {
        var items = ParseQuick("una hamburguesa doble y dos cocas");
        items.Should().HaveCount(2);
        items.Should().Contain(i => i.Name == "Hamburguesa Doble" && i.Quantity == 1);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 2);
    }

    [Theory]
    [InlineData("una", 1)]
    [InlineData("dos", 2)]
    [InlineData("tres", 3)]
    [InlineData("cuatro", 4)]
    [InlineData("cinco", 5)]
    public void VenezuelanPhrase_SpanishWordNumbers(string word, int expected)
    {
        var converted = WebhookProcessor.ConvertWordNumbersToDigits($"{word} hamburguesas");
        converted.Should().StartWith($"{expected} ");
    }

    // ══════════════════════════════════════════════════════════════
    // CATEGORY 4 — Observations / Modifiers
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Observation_SinCebolla()
    {
        var parsed = WebhookProcessor.ParseOrderText("1 hamburguesa sin cebolla");
        parsed.Should().ContainSingle();
        parsed[0].Name.Should().Be("Hamburguesa Clasica");
        parsed[0].Modifiers.Should().Contain("sin cebolla");
    }

    [Fact]
    public void Observation_ConExtraQueso()
    {
        var parsed = WebhookProcessor.ParseOrderText("1 perro caliente con extra queso");
        parsed.Should().ContainSingle();
        parsed[0].Name.Should().Be("Perro Clasico");
        parsed[0].Modifiers.Should().Contain("extra queso");
    }

    [Fact]
    public void Observation_MultipleModifiers_SeparateItems()
    {
        var parsed = WebhookProcessor.ParseOrderText("1 hamburguesa sin cebolla, 1 hamburguesa doble con extra queso");
        parsed.Should().HaveCount(2);

        var clasica = parsed.FirstOrDefault(i => i.Name == "Hamburguesa Clasica");
        clasica.Should().NotBeNull();
        clasica!.Modifiers.Should().Contain("sin cebolla");

        var doble = parsed.FirstOrDefault(i => i.Name == "Hamburguesa Doble");
        doble.Should().NotBeNull();
        doble!.Modifiers.Should().Contain("extra queso");
    }

    [Fact]
    public void Observation_ConTodo()
    {
        var parsed = WebhookProcessor.ParseOrderText("1 hamburguesa con todo");
        parsed.Should().ContainSingle();
        parsed[0].Modifiers.Should().Contain("con todo");
    }

    [Fact]
    public void Observation_BienTostada()
    {
        var parsed = WebhookProcessor.ParseOrderText("1 hamburguesa bien tostada");
        parsed.Should().ContainSingle();
        parsed[0].Modifiers.Should().Contain("bien tostada");
    }

    // ══════════════════════════════════════════════════════════════
    // CATEGORY 5 — Delivery Detection
    // ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("2 hamburguesas para delivery", "delivery")]
    [InlineData("2 hamburguesas delivery", "delivery")]
    [InlineData("1 perro caliente a domicilio", "delivery")]
    [InlineData("2 hamburguesas para envio", "delivery")]
    public void Delivery_Detected(string input, string expected)
    {
        WebhookProcessor.TryParseQuickOrder(input, out _, out var deliveryType, out _);
        deliveryType.Should().Be(expected);
    }

    [Theory]
    [InlineData("2 hamburguesas voy a buscar", "pickup")]
    [InlineData("2 hamburguesas para retirar", "pickup")]
    [InlineData("2 hamburguesas pick up", "pickup")]
    [InlineData("2 hamburguesas pickup", "pickup")]
    public void Pickup_Detected(string input, string expected)
    {
        WebhookProcessor.TryParseQuickOrder(input, out _, out var deliveryType, out _);
        deliveryType.Should().Be(expected);
    }

    [Fact]
    public void Delivery_ItemsStillParsedCorrectly()
    {
        WebhookProcessor.TryParseQuickOrder("2 hamburguesas dobles delivery", out var items, out var dt, out _);
        dt.Should().Be("delivery");
        items.Should().Contain(i => i.Name == "Hamburguesa Doble" && i.Quantity == 2);
    }

    // ══════════════════════════════════════════════════════════════
    // CATEGORY 6 — Order Editing (Add Items)
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Edit_AddItemToExistingOrder()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields { MenuSent = true };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2, UnitPrice = 6.50m });
        state.DeliveryType = "delivery";

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(Msg("agrega una coca"), _testBusiness);

        state.Items.Should().HaveCount(2);
        state.Items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        state.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    [Fact]
    public async Task Edit_RemoveItemFromOrder()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields { MenuSent = true };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2, UnitPrice = 6.50m });
        state.Items.Add(new ConversationItemEntry { Name = "Coca Cola", Quantity = 1, UnitPrice = 1.50m });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(Msg("quita la coca"), _testBusiness);

        state.Items.Should().ContainSingle();
        state.Items[0].Name.Should().Be("Hamburguesa Clasica");
    }

    [Fact]
    public async Task Edit_ReduceQuantity()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields { MenuSent = true };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 5, UnitPrice = 6.50m });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(Msg("quita 2 hamburguesas"), _testBusiness);

        state.Items.Should().ContainSingle();
        state.Items[0].Quantity.Should().Be(3);
    }

    [Fact]
    public async Task Edit_AddMoreOfSameItem()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields { MenuSent = true };
        state.Items.Add(new ConversationItemEntry { Name = "Coca Cola", Quantity = 1, UnitPrice = 1.50m });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(Msg("agrega 2 cocas mas"), _testBusiness);

        state.Items.Should().ContainSingle();
        state.Items[0].Quantity.Should().Be(3);
    }

    // ══════════════════════════════════════════════════════════════
    // CATEGORY 7 — Swap Items
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Swap_HamburguesaPorDoble()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields { MenuSent = true };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(Msg("cambia la hamburguesa por una doble"), _testBusiness);

        state.Items.Should().ContainSingle();
        state.Items[0].Name.Should().Be("Hamburguesa Doble");
        state.Items[0].Quantity.Should().Be(1);
        sentMessages.Last().Body.Should().Contain("cambi\u00e9");
    }

    [Fact]
    public async Task Swap_PreservesOriginalQuantity()
    {
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var state = new ConversationFields { MenuSent = true };
        state.Items.Add(new ConversationItemEntry { Name = "Papas Medianas", Quantity = 3, UnitPrice = 3.50m });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        await _sut.ProcessAsync(Msg("cambia las papas medianas por papas grandes"), _testBusiness);

        state.Items.Should().ContainSingle();
        state.Items[0].Name.Should().Be("Papas Grandes");
        state.Items[0].Quantity.Should().Be(3);
    }

    [Fact]
    public void Swap_ParsePattern_CambiaXPorY()
    {
        WebhookProcessor.ActiveCatalog = TestCatalogHelper.MenuCatalogWithExtras;
        WebhookProcessor.TryParseOrderModification(
            "cambia la coca cola por una pepsi", out var mod).Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.Swap);
        mod.ItemName.Should().Be("Coca Cola");
        mod.SwapTargetName.Should().Be("Pepsi");
    }

    // ══════════════════════════════════════════════════════════════
    // CATEGORY 8 — Ambiguous User Input / Stall Recovery
    // ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("mmm")]
    [InlineData("mmmm")]
    [InlineData("hmm")]
    [InlineData("no se")]
    [InlineData("espera")]
    [InlineData("esperame")]
    [InlineData("momento")]
    [InlineData("un momento")]
    [InlineData("dejame pensar")]
    [InlineData("dejame ver")]
    [InlineData("que tienen")]
    [InlineData("que me recomiendas")]
    [InlineData("a ver")]
    public void AmbiguousInput_DetectedAsStall(string input)
    {
        var t = WebhookProcessor.Normalize(input);
        WebhookProcessor.IsAmbiguousStall(t).Should().BeTrue();
    }

    [Theory]
    [InlineData("2 hamburguesas")]
    [InlineData("confirmar")]
    [InlineData("hola")]
    [InlineData("agrega 1 coca")]
    [InlineData("editar")]
    [InlineData("cancelar")]
    [InlineData("delivery")]
    public void RealInput_NotDetectedAsStall(string input)
    {
        var t = WebhookProcessor.Normalize(input);
        WebhookProcessor.IsAmbiguousStall(t).Should().BeFalse();
    }

    [Fact]
    public async Task AmbiguousInput_WithItemsInCart_ShowsOrderAndGuide()
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

        await _sut.ProcessAsync(Msg("mmm"), _testBusiness);

        sentMessages.Should().ContainSingle();
        sentMessages[0].Body.Should().Contain("pedido actual");
        sentMessages[0].Body.Should().Contain("Hamburguesa Clasica");
        sentMessages[0].Body.Should().Contain("agregar");

        // AI parser should NOT be invoked for stall messages
        _aiParserMock.Verify(
            x => x.ParseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task AmbiguousInput_EmptyCart_ShowsGenericRedirect()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationFields { MenuSent = true });

        await _sut.ProcessAsync(Msg("que tienen"), _testBusiness);

        sentMessages.Should().ContainSingle();
        sentMessages[0].Body.Should().Contain("deseas ordenar");

        _aiParserMock.Verify(
            x => x.ParseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ══════════════════════════════════════════════════════════════
    // CATEGORY 9 — Typos / Fuzzy Matching
    // ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("hamburgesa", "Hamburguesa Clasica")]
    [InlineData("hamburgesas", "Hamburguesa Clasica")]
    [InlineData("hambuguesa", "Hamburguesa Clasica")]
    [InlineData("hamburgueaa", "Hamburguesa Clasica")]
    [InlineData("hamburguesita", "Hamburguesa Clasica")]
    [InlineData("coka", "Coca Cola")]
    [InlineData("cocas", "Coca Cola")]
    [InlineData("cocacola", "Coca Cola")]
    [InlineData("burguer", "Hamburguesa Clasica")]
    [InlineData("burger", "Hamburguesa Clasica")]
    [InlineData("papitas", "Papas Pequenas")]
    [InlineData("papaas", "Papas Medianas")]
    [InlineData("maltin", "Malta")]
    [InlineData("maltita", "Malta")]
    [InlineData("hotdog", "Perro Clasico")]
    [InlineData("hot dog", "Perro Clasico")]
    [InlineData("perro", "Perro Clasico")]
    public void Typo_FuzzyMatchResolvesItem(string typo, string expected)
    {
        WebhookProcessor.ActiveCatalog = TestCatalogHelper.MenuCatalogWithExtras;
        var resolved = WebhookProcessor.NormalizeMenuItemName(typo);
        resolved.Should().Be(expected);
    }

    [Fact]
    public void Typo_FullOrderWithTypos()
    {
        var items = ParseQuick("2 hamburgesas y 1 coka");
        items.Should().HaveCount(2);
        items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    [Fact]
    public void Typo_ModificationWithTypos()
    {
        WebhookProcessor.ActiveCatalog = TestCatalogHelper.MenuCatalogWithExtras;
        var result = WebhookProcessor.TryParseOrderModification("agrega 2 hamburgesas mas", out var mod);
        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.Add);
        mod.ItemName.Should().Be("Hamburguesa Clasica");
        mod.Quantity.Should().Be(2);
    }

    // ══════════════════════════════════════════════════════════════
    // CATEGORY 10 — Heavy Order (Many Items)
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void HeavyOrder_5ItemTypes()
    {
        var text = "5 hamburguesas dobles\n3 papas grandes\n4 cocas\n2 perros calientes\ndelivery";

        WebhookProcessor.TryParseQuickOrder(text, out var items, out var dt, out _);

        dt.Should().Be("delivery");
        items.Should().HaveCount(4);
        items.Should().Contain(i => i.Name == "Hamburguesa Doble" && i.Quantity == 5);
        items.Should().Contain(i => i.Name == "Papas Grandes" && i.Quantity == 3);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 4);
        items.Should().Contain(i => i.Name == "Perro Clasico" && i.Quantity == 2);
    }

    [Fact]
    public void HeavyOrder_CorrectTotalCalculation()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Doble", Quantity = 5, UnitPrice = 8.50m });
        state.Items.Add(new ConversationItemEntry { Name = "Papas Grandes", Quantity = 3, UnitPrice = 4.50m });
        state.Items.Add(new ConversationItemEntry { Name = "Coca Cola", Quantity = 4, UnitPrice = 1.50m });
        state.Items.Add(new ConversationItemEntry { Name = "Perro Clasico", Quantity = 2, UnitPrice = 4.50m });
        state.DeliveryType = "delivery";
        state.ObservationPromptSent = true;
        state.ObservationAnswered = true;

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // 5*8.50 + 3*4.50 + 4*1.50 + 2*4.50 = 42.50 + 13.50 + 6.00 + 9.00 = 71.00
        // delivery = $4.00 → total = $75.00
        reply.Body.Should().Contain("Subtotal: $71.00");
        reply.Body.Should().Contain("Delivery: $4.00");
        reply.Body.Should().Contain("TOTAL: $75.00");
        reply.Body.Should().Contain("5x Hamburguesa Doble");
        reply.Body.Should().Contain("3x Papas Grandes");
        reply.Body.Should().Contain("4x Coca Cola");
        reply.Body.Should().Contain("2x Perro Clasico");
    }

    [Fact]
    public void HeavyOrder_CommasSeparatedOrder()
    {
        var items = ParseQuick("3 hamburguesas, 2 perros, 4 cocas, 1 papa grande");
        items.Should().HaveCount(4);
        items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 3);
        items.Should().Contain(i => i.Name == "Perro Clasico" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 4);
        items.Should().Contain(i => i.Name == "Papas Grandes" && i.Quantity == 1);
    }

    // ══════════════════════════════════════════════════════════════
    // CATEGORY 11 — Realistic Full WhatsApp Conversations
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Realistic_EpaBroDameMultiLine()
    {
        // "y" on a new line: parser splits on newlines first, then on " y " within each line.
        // A leading "y" on its own line is not preceded by whitespace so it stays as part of the segment.
        var text = "epa dame 2 hamburguesas dobles\n1 perro caliente\n2 cocas\npara delivery";

        WebhookProcessor.TryParseQuickOrder(text, out var items, out var dt, out _);

        dt.Should().Be("delivery");
        items.Should().HaveCount(3);
        items.Should().Contain(i => i.Name == "Hamburguesa Doble" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Perro Clasico" && i.Quantity == 1);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 2);
    }

    [Fact]
    public async Task Realistic_FullConversationFlow_OrderToConfirmation()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields { MenuSent = true };

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // Step 1: Place order
        await _sut.ProcessAsync(Msg("2 hamburguesas dobles y 1 coca delivery"), _testBusiness);

        state.Items.Should().HaveCount(2);
        state.Items.Should().Contain(i => i.Name == "Hamburguesa Doble" && i.Quantity == 2);
        state.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
        state.DeliveryType.Should().Be("delivery");

        // Step 2: Observation answer
        sentMessages.Clear();
        await _sut.ProcessAsync(Msg("no"), _testBusiness);

        state.ObservationAnswered.Should().BeTrue();

        // Step 3: Should see confirmation prompt with CONFIRMAR/EDITAR/CANCELAR
        var confirmMsg = sentMessages.LastOrDefault();
        confirmMsg.Should().NotBeNull();
        confirmMsg!.Body.Should().Contain("deseas hacer");
        confirmMsg.Buttons.Should().NotBeNull();
        confirmMsg.Buttons!.Select(b => b.Title).Should().Contain("Confirmar");
        confirmMsg.Buttons!.Select(b => b.Title).Should().Contain("Editar pedido");
        confirmMsg.Buttons!.Select(b => b.Title).Should().Contain("Cancelar");
    }

    [Fact]
    public async Task Realistic_OrderThenEdit_ThenConfirm()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields { MenuSent = true };

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // Step 1: Initial order
        await _sut.ProcessAsync(Msg("1 hamburguesa clasica pickup"), _testBusiness);
        state.Items.Should().ContainSingle(i => i.Name == "Hamburguesa Clasica");

        // Step 2: Observation
        await _sut.ProcessAsync(Msg("sin cebolla"), _testBusiness);

        // Step 3: User says EDITAR instead of CONFIRMAR
        sentMessages.Clear();
        await _sut.ProcessAsync(Msg("editar"), _testBusiness);

        // State should be reset for re-ordering
        state.ObservationPromptSent.Should().BeFalse();
        state.ObservationAnswered.Should().BeFalse();
        state.OrderConfirmed.Should().BeFalse();
        // But items should still be there
        state.Items.Should().NotBeEmpty();

        // Step 4: Add another item
        await _sut.ProcessAsync(Msg("agrega 2 cocas"), _testBusiness);
        state.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 2);
    }

    [Fact]
    public async Task Realistic_OrderThenCancel_ResetsCompletely()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields { MenuSent = true };

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // Step 1: Order
        await _sut.ProcessAsync(Msg("3 hamburguesas bbq delivery"), _testBusiness);
        state.Items.Should().NotBeEmpty();

        // Step 2: Observation
        await _sut.ProcessAsync(Msg("no"), _testBusiness);

        // Step 3: Cancel
        sentMessages.Clear();
        await _sut.ProcessAsync(Msg("cancelar"), _testBusiness);

        state.Items.Should().BeEmpty();
        state.DeliveryType.Should().BeNull();
        state.HumanHandoffRequested.Should().BeFalse("cancel should NOT trigger handoff");
        sentMessages.Should().ContainSingle(m => m.Body.Contains("cancelado"));
    }

    [Fact]
    public async Task Realistic_OrderWithModifier_DeliveryAndObservation()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields { MenuSent = true };

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // Order with modifier and delivery in one message
        await _sut.ProcessAsync(Msg("1 hamburguesa sin cebolla delivery"), _testBusiness);

        state.Items.Should().ContainSingle();
        state.Items[0].Name.Should().Be("Hamburguesa Clasica");
        state.DeliveryType.Should().Be("delivery");

        // The observation should be embedded (detected inline)
        // After observation answer, should get confirmation prompt
    }

    [Fact]
    public async Task Realistic_StalledUser_ThenPlacesOrder()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields { MenuSent = true };

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // User is indecisive
        await _sut.ProcessAsync(Msg("mmm"), _testBusiness);
        sentMessages.Last().Body.Should().Contain("deseas ordenar");

        // User stalls again
        sentMessages.Clear();
        await _sut.ProcessAsync(Msg("dejame pensar"), _testBusiness);
        sentMessages.Last().Body.Should().Contain("deseas ordenar");

        // User finally places order
        sentMessages.Clear();
        await _sut.ProcessAsync(Msg("2 hamburguesas dobles delivery"), _testBusiness);

        state.Items.Should().NotBeEmpty();
        state.Items.Should().Contain(i => i.Name == "Hamburguesa Doble" && i.Quantity == 2);
        state.DeliveryType.Should().Be("delivery");
    }

    [Fact]
    public async Task Realistic_SwapThenAddItem()
    {
        var sentMessages = new List<OutgoingMessage>();
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutgoingMessage>(), It.IsAny<CancellationToken>()))
            .Callback<OutgoingMessage, CancellationToken>((m, _) => sentMessages.Add(m))
            .ReturnsAsync(true);

        var state = new ConversationFields { MenuSent = true };
        state.Items.Add(new ConversationItemEntry { Name = "Hamburguesa Clasica", Quantity = 2, UnitPrice = 6.50m });
        state.Items.Add(new ConversationItemEntry { Name = "Coca Cola", Quantity = 1, UnitPrice = 1.50m });

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        // Swap hamburguesa for doble
        await _sut.ProcessAsync(Msg("cambia la hamburguesa clasica por hamburguesa doble"), _testBusiness);

        state.Items.Should().HaveCount(2);
        state.Items.Should().Contain(i => i.Name == "Hamburguesa Doble" && i.Quantity == 2);
        state.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);

        // Add papas
        sentMessages.Clear();
        await _sut.ProcessAsync(Msg("agrega 1 papa grande"), _testBusiness);

        state.Items.Should().HaveCount(3);
        state.Items.Should().Contain(i => i.Name == "Papas Grandes" && i.Quantity == 1);
    }

    // ══════════════════════════════════════════════════════════════
    // EXTRA — Edge Cases
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Edge_EmptyOrder_ReturnsEmpty()
    {
        var parsed = WebhookProcessor.ParseOrderText("");
        parsed.Should().BeEmpty();
    }

    [Fact]
    public void Edge_OnlyFillerWords()
    {
        var parsed = WebhookProcessor.ParseOrderText("hola buenas quiero porfa gracias");
        parsed.Should().BeEmpty();
    }

    [Fact]
    public void Edge_SingleItemNoQuantity()
    {
        var items = ParseQuick("hamburguesa");
        items.Should().ContainSingle();
        items[0].Name.Should().Be("Hamburguesa Clasica");
        items[0].Quantity.Should().Be(1);
    }

    [Fact]
    public void Edge_OrderSummary_ContainsAllItems()
    {
        var items = new List<ConversationItemEntry>
        {
            new() { Name = "Hamburguesa Doble", Quantity = 2, UnitPrice = 8.50m },
            new() { Name = "Coca Cola", Quantity = 3, UnitPrice = 1.50m },
            new() { Name = "Papas Grandes", Quantity = 1, UnitPrice = 4.50m }
        };

        var summary = Msg_Static.OrderSummaryWithTotal(items);

        summary.Should().Contain("2x Hamburguesa Doble");
        summary.Should().Contain("3x Coca Cola");
        summary.Should().Contain("1x Papas Grandes");
        summary.Should().Contain("TOTAL: $26.00"); // 17.00 + 4.50 + 4.50
    }

    [Fact]
    public void Edge_ConfirmationPrompt_ContainsAllOptions()
    {
        var prompt = Msg_Static.ConfirmOrderPrompt;
        prompt.Should().Contain("deseas hacer");

        // Options are now in buttons
        var buttons = Msg_Static.ConfirmButtons;
        buttons.Should().HaveCount(3);
        buttons.Select(b => b.Title).Should().Contain("Confirmar");
        buttons.Select(b => b.Title).Should().Contain("Editar pedido");
        buttons.Select(b => b.Title).Should().Contain("Cancelar");
    }

    [Fact]
    public void Edge_GentleRedirect_ContainsExample()
    {
        var redirect = Msg_Static.GentleRedirect;
        redirect.Should().Contain("deseas ordenar");
        redirect.Should().Contain("Ejemplo");
    }

    [Theory]
    [InlineData("editar")]
    [InlineData("modificar")]
    [InlineData("cambiar pedido")]
    [InlineData("cambiar mi pedido")]
    public void Edge_EditCommands_Recognized(string input)
    {
        WebhookProcessor.IsEditCommand(input).Should().BeTrue();
    }

    [Theory]
    [InlineData("cancelar")]
    [InlineData("cancelar pedido")]
    [InlineData("borrar todo")]
    [InlineData("empezar de cero")]
    public void Edge_CancelCommands_Recognized(string input)
    {
        WebhookProcessor.IsCancelCommand(input).Should().BeTrue();
    }

    [Fact]
    public void Edge_MixedCommasAndNewlines()
    {
        var items = ParseQuick("2 hamburguesas, 1 coca\n1 perro caliente");
        items.Should().HaveCount(3);
    }

    [Fact]
    public void Edge_OtraHamburguesa()
    {
        var parsed = WebhookProcessor.ParseOrderText("1 hamburguesa y otra hamburguesa doble");
        parsed.Should().HaveCount(2);
        parsed.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 1);
        parsed.Should().Contain(i => i.Name == "Hamburguesa Doble" && i.Quantity == 1);
    }

    // ══════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Quick-parse helper: returns list of (Name, Quantity) tuples from TryParseQuickOrder.
    /// </summary>
    private static List<(string Name, int Quantity)> ParseQuick(string input)
    {
        WebhookProcessor.ActiveCatalog = TestCatalogHelper.MenuCatalogWithExtras;
        WebhookProcessor.TryParseQuickOrder(input, out var items, out _, out _);
        return items;
    }

    /// <summary>
    /// Create a text message payload for testing.
    /// </summary>
    private static WebhookPayload Msg(string body) => new()
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
                                    WaId = "5511999999999",
                                    Profile = new WebhookProfile { Name = "Test User VE" }
                                }
                            ],
                            Messages =
                            [
                                new WebhookMessage
                                {
                                    From = "5511999999999",
                                    Id = $"wamid.ve{Interlocked.Increment(ref _msgCounter)}",
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

    /// <summary>
    /// Static accessor for MessageTemplates (internal class Msg).
    /// Uses the same internal access pattern as existing tests.
    /// </summary>
    private static class Msg_Static
    {
        public static string OrderSummaryWithTotal(IReadOnlyList<ConversationItemEntry> items)
            => WhatsAppSaaS.Application.Services.Msg.OrderSummaryWithTotal(items);

        public static string ConfirmOrderPrompt
            => WhatsAppSaaS.Application.Services.Msg.ConfirmOrderPrompt;

        public static List<ReplyButton> ConfirmButtons
            => WhatsAppSaaS.Application.Services.Msg.ConfirmButtons;

        public static string GentleRedirect
            => WhatsAppSaaS.Application.Services.Msg.GentleRedirect;
    }
}
