using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.DTOs;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Application.Services;
using WhatsAppSaaS.Domain.Entities;
using AiOrderItem = WhatsAppSaaS.Application.DTOs.OrderItem;
using DomainOrder = WhatsAppSaaS.Domain.Entities.Order;
using OutMsg = WhatsAppSaaS.Domain.Entities.OutgoingMessage;

namespace WhatsAppSaaS.Api.Tests.Conversation;

/// <summary>
/// Regression tests for the production bug:
///   Customer wrote "me das 2 shawarmas de pollo 350 y 2 coacolas"
///   Bot result included a raw $0 "shawarma de pollo" line and still proceeded
///   through observation / delivery / checkout.
///
/// Phase-1 minimal fix:
///   1) Shawarma flavor (pollo / carne / mixto / kibbe / falafel / 3 sabores / 4 sabores)
///      is preserved as a modifier when the resolved canonical is a sized Shawarma
///      row that doesn't itself encode the flavor. Applies to both the quick-parse
///      path (ExtractItemAndModifiers) and the AI/Brain path (in ProcessAsync).
///   2) Customer-stated size token (350 / 200) promotes the alias-resolved canonical
///      to the matching sibling on the AI path too.
///   3) Any AI item that cannot be resolved to a priced catalog row is queued for
///      clarification — NEVER added as a $0 raw cart line. Resolved items in the
///      same message are kept; checkout is blocked until clarification.
/// </summary>
public sealed class FlavorAndUnresolvedClarificationTests : IDisposable
{
    private readonly Mock<IAiParser> _aiParserMock = new();
    private readonly Mock<IWhatsAppClient> _whatsAppClientMock = new();
    private readonly Mock<IOrderRepository> _orderRepositoryMock = new();
    private readonly Mock<IConversationStateStore> _stateStoreMock = new();
    private readonly Mock<IMenuRepository> _menuRepositoryMock = new();
    private readonly WebhookProcessor _sut;
    private readonly BusinessContext _testBusiness;

    // MenuItem entities matching ShawarmaCatalog, returned by the mocked
    // IMenuRepository so ProcessAsync's LoadBusinessMenuAsync produces a
    // real-tenant (non-demo) ActiveCatalog with our shawarma rows.
    private static List<MenuItem> ShawarmaMenuItems()
    {
        return ShawarmaCatalog.Select(e => new MenuItem
        {
            Id = Guid.NewGuid(),
            CategoryId = Guid.NewGuid(),
            Category = new MenuCategory { Name = e.Category ?? "" },
            Name = e.Canonical,
            Price = e.Price,
            IsAvailable = true,
            Aliases = e.Aliases.Select(a => new MenuItemAlias { Alias = a }).ToList(),
        }).ToList();
    }

    // La Boyera-shaped catalog: sized shawarma siblings + flavor-bearing variants +
    // a beverage. Mirrors the actual production menu shape from the prior audits.
    private static readonly WebhookProcessor.MenuEntry[] ShawarmaCatalog =
    [
        new()
        {
            Canonical = "Shawarma 200 grs",
            Aliases = new[] { "shawarma", "shawarma 200", "shawarma carne 200",
                "shawarma de carne", "shawarma de pollo", "shawarma mixto",
                "shawarma mixto 200", "shawarma pollo 200" },
            Category = "Shawarmas",
            Price = 4.50m
        },
        new()
        {
            Canonical = "Shawarma 350 grs",
            Aliases = new[] { "shawarma 350", "shawarma carne 350", "shawarma grande",
                "shawarma mixto 350", "shawarma pollo 350" },
            Category = "Shawarmas",
            Price = 8.00m
        },
        new()
        {
            Canonical = "Shawarma de Falafel",
            Aliases = new[] { "shawarma falafel" },
            Category = "Shawarmas",
            Price = 6.00m
        },
        new()
        {
            Canonical = "Shawarma de Kibbe frito",
            Aliases = new[] { "shawarma kibbe", "kibbe shawarma" },
            Category = "Shawarmas",
            Price = 8.00m
        },
        new()
        {
            Canonical = "Refresco bombita",
            Aliases = new[] { "coca bombita", "coca pequeña", "refresco pequeño" },
            Category = "Bebidas",
            Price = 1.50m
        },
    ];

    public FlavorAndUnresolvedClarificationTests()
    {
        _orderRepositoryMock
            .Setup(x => x.AddOrderAsync(It.IsAny<DomainOrder>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DomainOrder o, CancellationToken _) => o);

        _stateStoreMock
            .Setup(x => x.IsMessageProcessedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _stateStoreMock
            .Setup(x => x.MarkMessageProcessedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _stateStoreMock
            .Setup(x => x.SaveAsync(It.IsAny<string>(), It.IsAny<ConversationFields>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _whatsAppClientMock
            .Setup(x => x.SendTextMessageAsync(It.IsAny<OutMsg>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _menuRepositoryMock
            .Setup(x => x.GetAvailableItemsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ShawarmaMenuItems());

        _testBusiness = new BusinessContext(
            Guid.NewGuid(), "123456789", "test-token", "La Mina del Shawarma - Test",
            MenuPdfUrl: "https://test.example.com/menu.pdf");

        _sut = new WebhookProcessor(
            _aiParserMock.Object,
            _whatsAppClientMock.Object,
            _orderRepositoryMock.Object,
            _stateStoreMock.Object,
            new Mock<ILogger<WebhookProcessor>>().Object,
            menuRepository: _menuRepositoryMock.Object);

        WebhookProcessor.ActiveCatalog.Value = ShawarmaCatalog;
        WebhookProcessor.ActiveCatalogIsDemo.Value = false;
    }

    public void Dispose()
    {
        WebhookProcessor.ActiveCatalog.Value = null;
        WebhookProcessor.ActiveCatalogIsDemo.Value = false;
    }

    // ── ExtractShawarmaFlavor unit tests ────────────────────────────────────

    [Fact]
    public void Flavor_pollo_extracted_for_sized_shawarma_canonical()
        => WebhookProcessor.ExtractShawarmaFlavor("shawarma de pollo 350 gramos", "Shawarma 350 grs")
            .Should().Be("pollo");

    [Fact]
    public void Flavor_carne_extracted()
        => WebhookProcessor.ExtractShawarmaFlavor("shawarma de carne", "Shawarma 200 grs")
            .Should().Be("carne");

    [Fact]
    public void Flavor_mixto_extracted()
        => WebhookProcessor.ExtractShawarmaFlavor("shawarma mixto", "Shawarma 200 grs")
            .Should().Be("mixto");

    [Fact]
    public void Flavor_skipped_when_canonical_already_encodes_falafel()
        => WebhookProcessor.ExtractShawarmaFlavor("shawarma falafel", "Shawarma de Falafel")
            .Should().BeNull();

    [Fact]
    public void Flavor_skipped_when_canonical_already_encodes_kibbe()
        => WebhookProcessor.ExtractShawarmaFlavor("shawarma kibbe", "Shawarma de Kibbe frito")
            .Should().BeNull();

    [Fact]
    public void Flavor_skipped_when_canonical_is_not_a_shawarma()
        => WebhookProcessor.ExtractShawarmaFlavor("plato de pollo crispy", "Plato Pollo Crispy")
            .Should().BeNull();

    [Fact]
    public void Flavor_kibbe_frito_does_not_double_annotate()
        => WebhookProcessor.ExtractShawarmaFlavor("shawarma kibbe frito", "Shawarma de Kibbe frito")
            .Should().BeNull();

    [Fact]
    public void Flavor_3_sabores_extracted()
        => WebhookProcessor.ExtractShawarmaFlavor("shawarma 3 sabores 350", "Shawarma 350 grs")
            .Should().Be("3 sabores");

    [Fact]
    public void Flavor_4_sabores_extracted()
        => WebhookProcessor.ExtractShawarmaFlavor("shawarma cuatro sabores 350", "Shawarma 350 grs")
            .Should().Be("4 sabores");

    // ── Quick-parse end-to-end: flavor + size preserved through ParseOrderText ──

    [Fact]
    public void QuickParse_2_shawarmas_de_pollo_350_promotes_to_350grs_with_pollo()
    {
        var parsed = WebhookProcessor.ParseOrderText("2 shawarmas de pollo 350");

        parsed.Should().ContainSingle();
        parsed[0].Name.Should().Be("Shawarma 350 grs");
        parsed[0].Quantity.Should().Be(2);
        parsed[0].Modifiers.Should().Be("pollo");
    }

    [Fact]
    public void QuickParse_2_shawarmas_de_carne_350_carries_carne_modifier()
    {
        var parsed = WebhookProcessor.ParseOrderText("2 shawarmas de carne 350");

        parsed.Should().ContainSingle();
        parsed[0].Name.Should().Be("Shawarma 350 grs");
        parsed[0].Quantity.Should().Be(2);
        parsed[0].Modifiers.Should().Be("carne");
    }

    [Fact]
    public void QuickParse_1_shawarma_mixto_200_carries_mixto_modifier()
    {
        var parsed = WebhookProcessor.ParseOrderText("1 shawarma mixto 200");

        parsed.Should().ContainSingle();
        parsed[0].Name.Should().Be("Shawarma 200 grs");
        parsed[0].Quantity.Should().Be(1);
        parsed[0].Modifiers.Should().Be("mixto");
    }

    [Fact]
    public void QuickParse_shawarma_falafel_no_double_annotation()
    {
        var parsed = WebhookProcessor.ParseOrderText("1 shawarma falafel");

        parsed.Should().ContainSingle();
        parsed[0].Name.Should().Be("Shawarma de Falafel");
        // Canonical already encodes the flavor — no redundant "(falafel)" modifier.
        (parsed[0].Modifiers ?? "").Should().NotContain("falafel");
    }

    [Fact]
    public void QuickParse_shawarma_kibbe_resolves_and_does_not_double_annotate()
    {
        var parsed = WebhookProcessor.ParseOrderText("1 shawarma kibbe");

        parsed.Should().ContainSingle();
        parsed[0].Name.Should().Be("Shawarma de Kibbe frito");
        (parsed[0].Modifiers ?? "").Should().NotContain("kibbe");
    }

    // ── AI-path end-to-end: unresolved blocks checkout, drinks preserved ──
    // The production input "me das 2 shawarmas de pollo 350 y 2 coacolas" triggers
    // IsComplexOrderMessage (length≥30 + " y " conjunction + 2+ digit tokens), so
    // ProcessAsync calls the AI parser. The mock returns whatever items we plant.

    private ConversationFields PrimeForAiPath(string customerText, params (string Name, int Qty)[] aiItems)
    {
        var state = new ConversationFields { MenuSent = true };
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var ai = new AiParseResult
        {
            Intent = RestaurantIntent.OrderCreate,
            Confidence = 0.95,
            Args = new ParsedArgs
            {
                Order = new OrderArgs
                {
                    Items = aiItems.Select(a => new AiOrderItem { Name = a.Name, Quantity = a.Qty }).ToList()
                }
            }
        };
        _aiParserMock
            .Setup(x => x.ParseAsync(customerText, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ai);

        return state;
    }

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
                            Metadata = new WebhookMetadata { PhoneNumberId = "123456789" },
                            Messages =
                            [
                                new WebhookMessage
                                {
                                    Id = "wamid." + Guid.NewGuid().ToString("N"),
                                    From = "5841412345001",
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

    private List<string> CapturedOutgoingBodies()
    {
        return _whatsAppClientMock.Invocations
            .Where(i => i.Method.Name == nameof(IWhatsAppClient.SendTextMessageAsync))
            .Select(i => ((OutMsg)i.Arguments[0]).Body ?? string.Empty)
            .ToList();
    }

    [Fact]
    public async Task AI_resolved_drinks_kept_when_shawarma_unresolved_and_clarification_sent()
    {
        // Production-shaped text: triggers IsComplexOrderMessage so the AI route fires.
        // The AI's first item shares no tokens with any catalog alias (so the
        // last-chance ExtractItemAndModifiers fallback also can't rescue it).
        var customerText = "me das 2 frikandeles brasilenos y 2 cocas en lata";
        var state = PrimeForAiPath(customerText,
            ("frikandel brasileno especial", 2),  // intentionally unresolvable
            ("Refresco bombita", 2));             // exact canonical hit

        await _sut.ProcessAsync(MakePayload(customerText), _testBusiness);

        // Resolved item preserved.
        state.Items.Should().ContainSingle(i =>
            i.Name == "Refresco bombita" && i.Quantity == 2 && i.UnitPrice == 1.50m,
            "drinks the AI matched must stay in the cart");

        // No $0 raw item.
        state.Items.Should().NotContain(i => i.UnitPrice <= 0m,
            "no $0 raw item must reach the cart");
        state.Items.Should().NotContain(i => i.Name.Contains("frikandel", StringComparison.OrdinalIgnoreCase));

        // Clarification sent with the original unresolved phrase preserved verbatim.
        var outgoing = CapturedOutgoingBodies();
        outgoing.Should().Contain(b => b.Contains("no pude identificar", StringComparison.OrdinalIgnoreCase));
        outgoing.Should().Contain(b => b.Contains("frikandel brasileno especial", StringComparison.OrdinalIgnoreCase),
            "clarification must preserve the original unresolved phrase verbatim");

        // Bot did NOT advance to observation / delivery.
        outgoing.Should().NotContain(b => b.Contains("observac", StringComparison.OrdinalIgnoreCase));
        state.CheckoutFormSent.Should().BeFalse();
        state.OrderConfirmed.Should().BeFalse();
    }

    [Fact]
    public async Task AI_unresolved_only_message_blocks_checkout_and_keeps_cart_empty()
    {
        // Length≥30 + 2+ digit tokens + " y " conjunction → AI route.
        var customerText = "quiero 2 frikandel especial y 1 cosa rara mas";
        var state = PrimeForAiPath(customerText, ("frikandel especial", 2), ("cosa rara", 1));

        await _sut.ProcessAsync(MakePayload(customerText), _testBusiness);

        state.Items.Should().BeEmpty("nothing resolvable, nothing in cart");
        state.Items.Should().NotContain(i => i.UnitPrice <= 0m);

        var outgoing = CapturedOutgoingBodies();
        outgoing.Should().Contain(b => b.Contains("no pude identificar", StringComparison.OrdinalIgnoreCase));
        outgoing.Should().Contain(b => b.Contains("frikandel especial", StringComparison.OrdinalIgnoreCase));
        outgoing.Should().NotContain(b => b.Contains("observac", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AI_resolves_shawarma_de_pollo_350_to_350grs_with_pollo_modifier()
    {
        // Production-symptom input.
        var customerText = "me das 2 shawarmas de pollo 350 y 2 cocas en lata";
        var state = PrimeForAiPath(customerText,
            ("shawarma de pollo 350", 2),
            ("Refresco bombita", 2));

        await _sut.ProcessAsync(MakePayload(customerText), _testBusiness);

        state.Items.Should().Contain(i =>
            i.Name == "Shawarma 350 grs"
            && i.Quantity == 2
            && i.UnitPrice == 8.00m
            && i.Modifiers == "pollo");
        state.Items.Should().Contain(i =>
            i.Name == "Refresco bombita" && i.Quantity == 2 && i.UnitPrice == 1.50m);
        state.Items.Should().NotContain(i => i.UnitPrice <= 0m);
    }
}
