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
/// Tests for per-item modifier distribution during the observation stage.
/// Covers: "extra de queso en una", "en la otra", "la primera con",
/// "la segunda con", per-item splitting, typo tolerance, pricing.
/// </summary>
public class PerItemModifierTests
{
    private readonly Mock<IAiParser> _aiParserMock;
    private readonly Mock<IWhatsAppClient> _whatsAppClientMock;
    private readonly Mock<IOrderRepository> _orderRepositoryMock;
    private readonly Mock<IConversationStateStore> _stateStoreMock;
    private readonly WebhookProcessor _sut;
    private readonly BusinessContext _testBusiness;

    private static int _msgCounter;

    public PerItemModifierTests()
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

        WebhookProcessor.ActiveCatalog = TestCatalogHelper.MenuCatalogWithExtras;
    }

    // ═══════════════════════════════════════════════════════════
    //   Test 1: "extra de queso en una" — single modifier on one
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Test1_ExtraQuesoEnUna_SingleModifier()
    {
        var state = MakeStateWithItems("Hamburguesa Clasica", 2, 6.50m);

        var applied = WebhookProcessor.TryApplyPerItemModifiers(state,
            "extra de queso en una");

        applied.Should().BeTrue();
        // Should split 2x into two 1x items
        state.Items.Should().HaveCount(2);
        // One should have extra queso, the other no modifier
        var withMod = state.Items.Where(i => !string.IsNullOrWhiteSpace(i.Modifiers)).ToList();
        var withoutMod = state.Items.Where(i => string.IsNullOrWhiteSpace(i.Modifiers)).ToList();
        withMod.Should().HaveCount(1);
        withoutMod.Should().HaveCount(1);
        withMod[0].Modifiers.Should().Contain("extra queso");
        withMod[0].UnitPrice.Should().Be(7.50m, "6.50 + 1.00 extra queso");
        withoutMod[0].UnitPrice.Should().Be(6.50m);
    }

    // ═══════════════════════════════════════════════════════════
    //   Test 2: "extra de queso en una y extra de tocineta en la otra"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Test2_ExtraQuesoAndTocinetaSplit()
    {
        var state = MakeStateWithItems("Hamburguesa Clasica", 2, 6.50m);

        var applied = WebhookProcessor.TryApplyPerItemModifiers(state,
            "extra de queso en una y extra de tocineta en la otra");

        applied.Should().BeTrue();
        state.Items.Should().HaveCount(2);

        var item1 = state.Items.First(i => i.Modifiers != null && i.Modifiers.Contains("extra queso") && !i.Modifiers.Contains("extra tocineta"));
        var item2 = state.Items.First(i => i.Modifiers != null && i.Modifiers.Contains("extra tocineta"));

        item1.UnitPrice.Should().Be(7.50m, "6.50 + 1.00 extra queso = 7.50");
        item2.UnitPrice.Should().Be(8.00m, "6.50 + 1.50 extra tocineta = 8.00");

        // Total increase: 1.00 + 1.50 = 2.50
        var totalIncrease = state.Items.Sum(i => i.UnitPrice * i.Quantity) - (2 * 6.50m);
        totalIncrease.Should().Be(2.50m);
    }

    // ═══════════════════════════════════════════════════════════
    //   Test 3: Multi-line — "extra de queso en una\nextra de queso + extra de tocineta en la otra"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Test3_MultiLine_CompoundModifiers()
    {
        var state = MakeStateWithItems("Hamburguesa Clasica", 2, 6.50m);

        var applied = WebhookProcessor.TryApplyPerItemModifiers(state,
            "extra de queso en una\nextra de queso + extra de tocineta en la otra");

        applied.Should().BeTrue();
        state.Items.Should().HaveCount(2);

        // Burger 1: extra queso only → 6.50 + 1.00 = 7.50
        var burger1 = state.Items.FirstOrDefault(i =>
            i.Modifiers != null && i.Modifiers.Contains("extra queso") && !i.Modifiers.Contains("extra tocineta"));
        burger1.Should().NotBeNull();
        burger1!.UnitPrice.Should().Be(7.50m);

        // Burger 2: extra queso + extra tocineta → 6.50 + 1.00 + 1.50 = 9.00
        var burger2 = state.Items.FirstOrDefault(i =>
            i.Modifiers != null && i.Modifiers.Contains("extra queso") && i.Modifiers.Contains("extra tocineta"));
        burger2.Should().NotBeNull();
        burger2!.UnitPrice.Should().Be(9.00m);

        // Total increase: 1.00 + (1.00 + 1.50) = 3.50
        var totalIncrease = state.Items.Sum(i => i.UnitPrice * i.Quantity) - (2 * 6.50m);
        totalIncrease.Should().Be(3.50m);
    }

    // ═══════════════════════════════════════════════════════════
    //   Test 4: "la primera con extra queso y la segunda con extra tocineta"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Test4_PrimeraSegunda_PerItemAssignment()
    {
        var state = MakeStateWithItems("Hamburguesa Clasica", 2, 6.50m);

        var applied = WebhookProcessor.TryApplyPerItemModifiers(state,
            "la primera con extra queso y la segunda con extra tocineta");

        applied.Should().BeTrue();
        state.Items.Should().HaveCount(2);

        var withQueso = state.Items.First(i => i.Modifiers != null && i.Modifiers.Contains("extra queso") && !i.Modifiers.Contains("extra tocineta"));
        var withTocineta = state.Items.First(i => i.Modifiers != null && i.Modifiers.Contains("extra tocineta"));

        withQueso.UnitPrice.Should().Be(7.50m);
        withTocineta.UnitPrice.Should().Be(8.00m);
    }

    // ═══════════════════════════════════════════════════════════
    //   Test 5: Typo "rocineta" → fuzzy matches to "tocineta"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Test5_TypoRocineta_FuzzyMatchesToTocineta()
    {
        var state = MakeStateWithItems("Hamburguesa Clasica", 2, 6.50m);

        var applied = WebhookProcessor.TryApplyPerItemModifiers(state,
            "extra de queso en una y extra de rocineta en la otra");

        applied.Should().BeTrue();
        state.Items.Should().HaveCount(2);

        // "rocineta" should fuzzy-match to "tocineta"
        var withTocineta = state.Items.FirstOrDefault(i =>
            i.Modifiers != null && i.Modifiers.Contains("extra tocineta", StringComparison.OrdinalIgnoreCase));
        withTocineta.Should().NotBeNull("rocineta should fuzzy-match to tocineta");
        withTocineta!.UnitPrice.Should().Be(8.00m, "6.50 + 1.50 extra tocineta = 8.00");

        // Should NOT map to any unrelated product
        state.Items.Should().NotContain(i => i.Name == "Malta");
    }

    // ═══════════════════════════════════════════════════════════
    //   Test 6: Non-burger items — 2 perros calientes
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Test6_PerrosCalientes_PerItemModifiers()
    {
        var state = MakeStateWithItems("Perro Clasico", 2, 4.50m);

        var applied = WebhookProcessor.TryApplyPerItemModifiers(state,
            "uno sin cebolla y el otro con extra queso");

        applied.Should().BeTrue();
        state.Items.Should().HaveCount(2);

        var sinCebolla = state.Items.FirstOrDefault(i =>
            i.Modifiers != null && i.Modifiers.Contains("sin cebolla"));
        sinCebolla.Should().NotBeNull();
        sinCebolla!.UnitPrice.Should().Be(4.50m, "sin cebolla has no extra cost");

        var conQueso = state.Items.FirstOrDefault(i =>
            i.Modifiers != null && i.Modifiers.Contains("extra queso"));
        conQueso.Should().NotBeNull();
        conQueso!.UnitPrice.Should().Be(5.50m, "4.50 + 1.00 extra queso = 5.50");
    }

    // ═══════════════════════════════════════════════════════════
    //   Integration: observation handler applies modifiers
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task ObservationStage_ExtraQuesoEnUna_StoredAsObservation()
    {
        var state = new ConversationFields
        {
            MenuSent = true,
            Items = new List<ConversationItemEntry>
            {
                new() { Name = "Hamburguesa Clasica", Quantity = 2, UnitPrice = 6.50m }
            },
            DeliveryType = "delivery",
            ObservationPromptSent = true,
            ObservationAnswered = false
        };
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var payload = MakePayload("extra de queso en una\nextra de queso + extra de tocineta en la otra");
        await _sut.ProcessAsync(payload, _testBusiness);

        // In the new observation flow, text is stored as-is (no structured parsing)
        state.Items.Should().HaveCount(1, "items should not be split in observation flow");
        state.SpecialInstructions.Should().Contain("extra de queso",
            "observation text should be stored in SpecialInstructions");
        state.ObservationAnswered.Should().BeTrue();
    }

    [Fact]
    public async Task ObservationStage_NoObservation_StillWorks()
    {
        var state = new ConversationFields
        {
            MenuSent = true,
            Items = new List<ConversationItemEntry>
            {
                new() { Name = "Hamburguesa Clasica", Quantity = 2, UnitPrice = 6.50m }
            },
            DeliveryType = "delivery",
            ObservationPromptSent = true,
            ObservationAnswered = false
        };
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var payload = MakePayload("no");
        await _sut.ProcessAsync(payload, _testBusiness);

        // "no" = no observation — items unchanged
        state.Items.Should().HaveCount(1);
        state.Items[0].Quantity.Should().Be(2);
        state.Items[0].UnitPrice.Should().Be(6.50m);
    }

    [Fact]
    public async Task ObservationStage_PlainText_FallbackToSpecialInstructions()
    {
        var state = new ConversationFields
        {
            MenuSent = true,
            Items = new List<ConversationItemEntry>
            {
                new() { Name = "Hamburguesa Clasica", Quantity = 2, UnitPrice = 6.50m }
            },
            DeliveryType = "delivery",
            ObservationPromptSent = true,
            ObservationAnswered = false
        };
        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var payload = MakePayload("bien cocidas por favor");
        await _sut.ProcessAsync(payload, _testBusiness);

        // Plain text without per-item targeting → stored as SpecialInstructions
        state.SpecialInstructions.Should().Contain("bien cocidas por favor");
        // Items unchanged
        state.Items.Should().HaveCount(1);
        state.Items[0].Quantity.Should().Be(2);
    }

    // ═══════════════════════════════════════════════════════════
    //   Edge cases
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void EdgeCase_3Hamburguesas_2Segments_LeavesRemaining()
    {
        var state = MakeStateWithItems("Hamburguesa Clasica", 3, 6.50m);

        var applied = WebhookProcessor.TryApplyPerItemModifiers(state,
            "extra de queso en una y extra de tocineta en la otra");

        applied.Should().BeTrue();
        // 3 total: 2 split + 1 remaining
        state.Items.Should().HaveCount(3);
        state.Items.Where(i => string.IsNullOrWhiteSpace(i.Modifiers)).Should().HaveCount(1);
        state.Items.First(i => string.IsNullOrWhiteSpace(i.Modifiers)).UnitPrice.Should().Be(6.50m);
    }

    [Fact]
    public void EdgeCase_UnaConQuesoYLaOtraConQuesoYTocineta()
    {
        var state = MakeStateWithItems("Hamburguesa Clasica", 2, 6.50m);

        var applied = WebhookProcessor.TryApplyPerItemModifiers(state,
            "una con extra queso y la otra con extra queso y extra tocineta");

        applied.Should().BeTrue();
        state.Items.Should().HaveCount(2);

        // Both have queso, one also has tocineta
        state.Items.All(i => i.Modifiers != null && i.Modifiers.Contains("extra queso")).Should().BeTrue();
        state.Items.Count(i => i.Modifiers != null && i.Modifiers.Contains("extra tocineta")).Should().Be(1);
    }

    [Fact]
    public void ParseSegments_SimpleExtraQuesoEnUna_OneSegment()
    {
        var segments = WebhookProcessor.ParseModifierSegments("extra de queso en una");

        segments.Should().HaveCount(1);
        segments[0].Modifiers.Should().ContainSingle();
        segments[0].Modifiers[0].Text.Should().Contain("extra queso");
        segments[0].Modifiers[0].Price.Should().Be(1.00m);
    }

    [Fact]
    public void ParseSegments_TwoSegments_NewlineSeparated()
    {
        var segments = WebhookProcessor.ParseModifierSegments(
            "extra de queso en una\nextra de tocineta en la otra");

        segments.Should().HaveCount(2);
        segments[0].Modifiers[0].Text.Should().Contain("extra queso");
        segments[1].Modifiers[0].Text.Should().Contain("extra tocineta");
    }

    [Fact]
    public void ParseSegments_CompoundPlusSign()
    {
        var segments = WebhookProcessor.ParseModifierSegments(
            "extra de queso + extra de tocineta en la otra");

        segments.Should().HaveCount(1);
        segments[0].Modifiers.Should().HaveCount(2);
    }

    [Fact]
    public void ResolveModifier_Rocineta_FuzzyToTocineta()
    {
        // "rocineta" is now an explicit alias, so should resolve
        var resolved = WebhookProcessor.NormalizeMenuItemName("extra rocineta");
        resolved.Should().Be("Extra Tocineta");
    }

    // ═══════════════════════════════════════════════════════════
    //   Helpers
    // ═══════════════════════════════════════════════════════════

    private static ConversationFields MakeStateWithItems(string itemName, int qty, decimal unitPrice)
    {
        return new ConversationFields
        {
            MenuSent = true,
            Items = new List<ConversationItemEntry>
            {
                new() { Name = itemName, Quantity = qty, UnitPrice = unitPrice }
            },
            DeliveryType = "delivery",
            ObservationPromptSent = true,
            ObservationAnswered = false
        };
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
                            Metadata = new WebhookMetadata
                            {
                                PhoneNumberId = "123456789"
                            },
                            Messages =
                            [
                                new WebhookMessage
                                {
                                    Id = $"wamid.pim-{Interlocked.Increment(ref _msgCounter)}",
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
