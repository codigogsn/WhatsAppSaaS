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

        _testBusiness = new BusinessContext(Guid.NewGuid(), "123456789", "test-token", "Mi Restaurante");

        _sut = new WebhookProcessor(
            _aiParserMock.Object,
            _whatsAppClientMock.Object,
            _orderRepositoryMock.Object,
            _stateStoreMock.Object,
            new Mock<ILogger<WebhookProcessor>>().Object);

        // Set active catalog for static helper access
        WebhookProcessor.ActiveCatalog = WebhookProcessor.MenuCatalog;
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
    public async Task Modifier_ExtraQuesoEnHamburguesas_AppliedCorrectly()
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

        var payload = MakePayload("extra queso en las 2 hamburguesas");
        await _sut.ProcessAsync(payload, _testBusiness);

        // Modifier should be applied to the hamburguesas
        var hamburguesas = state.Items.FirstOrDefault(i => i.Name == "Hamburguesa Clasica");
        hamburguesas.Should().NotBeNull();
        hamburguesas!.Modifiers.Should().Contain("extra queso");
        // Price should increase by $1.00 per unit (Extra Queso = $1.00)
        hamburguesas.UnitPrice.Should().Be(7.50m, "6.50 base + 1.00 extra queso = 7.50");
        // Should NOT create a separate Malta or other beverage
        state.Items.Should().NotContain(i => i.Name == "Malta");
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
    public async Task Correction_NoAgregasteExtraQueso_AppliedToExistingItems()
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

        var payload = MakePayload("no agregaste el extra de queso");
        await _sut.ProcessAsync(payload, _testBusiness);

        state.Items.Should().HaveCount(1);
        state.Items[0].Modifiers.Should().Contain("extra queso");
        state.Items[0].UnitPrice.Should().Be(7.50m, "6.50 + 1.00 extra queso");
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
}
