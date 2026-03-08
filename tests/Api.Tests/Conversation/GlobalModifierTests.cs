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
/// Tests for grouped/global modifier application.
/// Covers: "extra queso en las 3 hamburguesas", "en todas las hamburguesas",
/// "en cada una de las hamburguesas", correction intent with target, safety net.
/// </summary>
public class GlobalModifierTests
{
    private readonly Mock<IAiParser> _aiParserMock;
    private readonly Mock<IWhatsAppClient> _whatsAppClientMock;
    private readonly Mock<IOrderRepository> _orderRepositoryMock;
    private readonly Mock<IConversationStateStore> _stateStoreMock;
    private readonly WebhookProcessor _sut;
    private readonly BusinessContext _testBusiness;

    private static int _msgCounter;

    public GlobalModifierTests()
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

        WebhookProcessor.ActiveCatalog = WebhookProcessor.MenuCatalog;
    }

    // ═══════════════════════════════════════════════════════════
    //   Test 1: "extra de queso en las 3 hamburguesas"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Test1_ExtraDeQuesoEnLas3Hamburguesas_ObservationStage()
    {
        var state = MakeObservationState("Hamburguesa Clasica", 3, 6.50m);

        var applied = WebhookProcessor.TryApplyPerItemModifiers(state,
            "extra de queso en las 3 hamburguesas");

        applied.Should().BeTrue("global modifier should be recognized and applied");
        state.Items.Should().HaveCount(1, "all 3 stay as one line item");
        state.Items[0].Modifiers.Should().Contain("extra queso");
        state.Items[0].UnitPrice.Should().Be(7.50m, "6.50 + 1.00 extra queso per unit");

        var totalIncrease = state.Items.Sum(i => i.UnitPrice * i.Quantity) - (3 * 6.50m);
        totalIncrease.Should().Be(3.00m, "3 units x $1.00 extra = $3.00 total increase");
    }

    // ═══════════════════════════════════════════════════════════
    //   Test 2: "extra queso en las 3 hamburguesas" (no "de")
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Test2_ExtraQuesoEnLas3Hamburguesas_NoDe()
    {
        var state = MakeObservationState("Hamburguesa Clasica", 3, 6.50m);

        var applied = WebhookProcessor.TryApplyPerItemModifiers(state,
            "extra queso en las 3 hamburguesas");

        applied.Should().BeTrue();
        state.Items[0].Modifiers.Should().Contain("extra queso");
        state.Items[0].UnitPrice.Should().Be(7.50m);

        var totalIncrease = state.Items.Sum(i => i.UnitPrice * i.Quantity) - (3 * 6.50m);
        totalIncrease.Should().Be(3.00m);
    }

    // ═══════════════════════════════════════════════════════════
    //   Test 3: "extra de queso en cada una de las hamburguesas"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Test3_ExtraDeQuesoEnCadaUna()
    {
        var state = MakeObservationState("Hamburguesa Clasica", 3, 6.50m);

        var applied = WebhookProcessor.TryApplyPerItemModifiers(state,
            "extra de queso en cada una de las hamburguesas");

        applied.Should().BeTrue();
        state.Items[0].Modifiers.Should().Contain("extra queso");
        state.Items[0].UnitPrice.Should().Be(7.50m);

        var totalIncrease = state.Items.Sum(i => i.UnitPrice * i.Quantity) - (3 * 6.50m);
        totalIncrease.Should().Be(3.00m);
    }

    // ═══════════════════════════════════════════════════════════
    //   Test 4: "extra queso en todas las hamburguesas"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Test4_ExtraQuesoEnTodasLas()
    {
        var state = MakeObservationState("Hamburguesa Clasica", 3, 6.50m);

        var applied = WebhookProcessor.TryApplyPerItemModifiers(state,
            "extra queso en todas las hamburguesas");

        applied.Should().BeTrue();
        state.Items[0].Modifiers.Should().Contain("extra queso");
        state.Items[0].UnitPrice.Should().Be(7.50m);

        var totalIncrease = state.Items.Sum(i => i.UnitPrice * i.Quantity) - (3 * 6.50m);
        totalIncrease.Should().Be(3.00m);
    }

    // ═══════════════════════════════════════════════════════════
    //   Test 5: Correction intent with target —
    //   "falto el extra de queso en cada una de las hamburguesas"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Test5_CorrectionIntent_WithTarget()
    {
        var result = WebhookProcessor.TryParseOrderModification(
            "falto el extra de queso en cada una de las hamburguesas", out var mod);

        result.Should().BeTrue("correction intent with target should be detected");
        mod.Type.Should().Be(WebhookProcessor.ModificationType.AddModifier);
        mod.ModifierText.Should().Be("extra queso");
        mod.TargetItemName.Should().Be("Hamburguesa Clasica");
    }

    [Fact]
    public async Task Test5_CorrectionIntent_Integration()
    {
        var state = MakeModificationState("Hamburguesa Clasica", 3, 6.50m);

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var payload = MakePayload("falto el extra de queso en cada una de las hamburguesas");
        await _sut.ProcessAsync(payload, _testBusiness);

        state.Items.Should().HaveCount(1, "no new products created");
        state.Items[0].Name.Should().Be("Hamburguesa Clasica");
        state.Items[0].Modifiers.Should().Contain("extra queso");
        state.Items[0].UnitPrice.Should().Be(7.50m);
    }

    // ═══════════════════════════════════════════════════════════
    //   Test 6: "no agregaste el extra de queso en las 3 hamburguesas"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Test6_NoAgregaste_WithTarget()
    {
        var result = WebhookProcessor.TryParseOrderModification(
            "no agregaste el extra de queso en las 3 hamburguesas", out var mod);

        result.Should().BeTrue("'no agregaste' correction with target should be detected");
        mod.Type.Should().Be(WebhookProcessor.ModificationType.AddModifier);
        mod.ModifierText.Should().Be("extra queso");
        mod.TargetItemName.Should().Be("Hamburguesa Clasica");
    }

    [Fact]
    public async Task Test6_NoAgregaste_Integration_NoMalta()
    {
        var state = MakeModificationState("Hamburguesa Clasica", 3, 6.50m);

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var payload = MakePayload("no agregaste el extra de queso en las 3 hamburguesas");
        await _sut.ProcessAsync(payload, _testBusiness);

        state.Items.Should().HaveCount(1, "should NOT create Malta or unrelated product");
        state.Items[0].Name.Should().Be("Hamburguesa Clasica");
        state.Items[0].Modifiers.Should().Contain("extra queso");
        // No Malta
        state.Items.Should().NotContain(i => i.Name.Contains("Malta", StringComparison.OrdinalIgnoreCase));
    }

    // ═══════════════════════════════════════════════════════════
    //   Test 7: "extra queso en todos los perros" — non-burger target
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Test7_ExtraQuesoEnTodosLosPerros()
    {
        var state = MakeObservationState("Perro Clasico", 2, 4.50m);

        var applied = WebhookProcessor.TryApplyPerItemModifiers(state,
            "extra queso en todos los perros");

        applied.Should().BeTrue("grouped modifier should work for perros too");
        state.Items[0].Modifiers.Should().Contain("extra queso");
        state.Items[0].UnitPrice.Should().Be(5.50m, "4.50 + 1.00 extra queso");

        var totalIncrease = state.Items.Sum(i => i.UnitPrice * i.Quantity) - (2 * 4.50m);
        totalIncrease.Should().Be(2.00m);
    }

    // ═══════════════════════════════════════════════════════════
    //   Test 8: Safety — "extra queso en las hamburguesas" (no qty)
    //   Should apply to all matching, never create unrelated product
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Test8_ExtraQuesoEnLasHamburguesas_NoQty_ObservationStage()
    {
        var state = MakeObservationState("Hamburguesa Clasica", 3, 6.50m);

        var applied = WebhookProcessor.TryApplyPerItemModifiers(state,
            "extra queso en las hamburguesas");

        applied.Should().BeTrue("modifier should be applied even without explicit qty");
        state.Items[0].Modifiers.Should().Contain("extra queso");
        state.Items[0].UnitPrice.Should().Be(7.50m);
        // No unrelated products
        state.Items.Should().NotContain(i => i.Name.Contains("Malta", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Test8_SafetyNet_ModificationStage_NoMalta()
    {
        // At modification stage, "extra queso en las hamburguesas" should apply modifier,
        // NOT create Malta or any unrelated product
        var state = MakeModificationState("Hamburguesa Clasica", 3, 6.50m);

        _stateStoreMock
            .Setup(x => x.GetOrCreateAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(state);

        var payload = MakePayload("extra queso en las hamburguesas");
        await _sut.ProcessAsync(payload, _testBusiness);

        state.Items.Should().NotContain(i => i.Name.Contains("Malta", StringComparison.OrdinalIgnoreCase),
            "modifier intent must NEVER create an unrelated product");
        state.Items[0].Name.Should().Be("Hamburguesa Clasica");
    }

    // ═══════════════════════════════════════════════════════════
    //   HasModifierIntent safety tests
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("extra de queso en las 3 hamburguesas")]
    [InlineData("extra queso en todas las hamburguesas")]
    [InlineData("extra queso en cada una de las hamburguesas")]
    [InlineData("falto el extra de queso en las hamburguesas")]
    [InlineData("no agregaste el extra de queso en las hamburguesas")]
    public void HasModifierIntent_TrueForGroupedModifiers(string input)
    {
        WebhookProcessor.HasModifierIntent(input).Should().BeTrue(
            $"\"{input}\" should be recognized as modifier intent");
    }

    [Theory]
    [InlineData("3 hamburguesas clasicas")]
    [InlineData("quiero una coca cola")]
    [InlineData("hola buenas")]
    public void HasModifierIntent_FalseForNonModifiers(string input)
    {
        WebhookProcessor.HasModifierIntent(input).Should().BeFalse(
            $"\"{input}\" should NOT be recognized as modifier intent");
    }

    // ═══════════════════════════════════════════════════════════
    //   Pattern 6 fix: "extra de queso" captured correctly
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Pattern6_ExtraDeQueso_ParsedCorrectly()
    {
        var result = WebhookProcessor.TryParseOrderModification(
            "extra de queso en las 3 hamburguesas", out var mod);

        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.AddModifier);
        mod.ModifierText.Should().Be("extra queso", "should normalize 'extra de queso' to 'extra queso'");
        mod.TargetItemName.Should().Be("Hamburguesa Clasica");
    }

    [Fact]
    public void Pattern6_PonleExtraQueso_ParsedCorrectly()
    {
        var result = WebhookProcessor.TryParseOrderModification(
            "ponle extra queso a las 3 hamburguesas", out var mod);

        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.AddModifier);
        mod.ModifierText.Should().Be("extra queso");
        mod.TargetItemName.Should().Be("Hamburguesa Clasica");
    }

    // ═══════════════════════════════════════════════════════════
    //   Multi-item order: modifier only affects target family
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void GlobalModifier_OnlyAffectsTargetItems()
    {
        var state = new ConversationFields
        {
            MenuSent = true,
            Items = new List<ConversationItemEntry>
            {
                new() { Name = "Hamburguesa Clasica", Quantity = 3, UnitPrice = 6.50m },
                new() { Name = "Papas Grandes", Quantity = 2, UnitPrice = 3.50m },
                new() { Name = "Coca Cola", Quantity = 4, UnitPrice = 2.00m }
            },
            DeliveryType = "delivery",
            ObservationPromptSent = true,
            ObservationAnswered = false
        };

        var applied = WebhookProcessor.TryApplyPerItemModifiers(state,
            "extra de queso en las 3 hamburguesas");

        applied.Should().BeTrue();
        var burger = state.Items.First(i => i.Name == "Hamburguesa Clasica");
        burger.Modifiers.Should().Contain("extra queso");
        burger.UnitPrice.Should().Be(7.50m);

        // Other items should NOT be affected
        var papas = state.Items.First(i => i.Name == "Papas Grandes");
        papas.Modifiers.Should().BeNullOrEmpty();
        papas.UnitPrice.Should().Be(3.50m);

        var coca = state.Items.First(i => i.Name == "Coca Cola");
        coca.Modifiers.Should().BeNullOrEmpty();
        coca.UnitPrice.Should().Be(2.00m);
    }

    // ═══════════════════════════════════════════════════════════
    //   Helpers
    // ═══════════════════════════════════════════════════════════

    /// <summary>State at observation stage (bot asked for observations, user hasn't answered yet).</summary>
    private static ConversationFields MakeObservationState(string itemName, int qty, decimal unitPrice)
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

    /// <summary>State at modification stage (observation already answered, order shown, user sends correction).</summary>
    private static ConversationFields MakeModificationState(string itemName, int qty, decimal unitPrice)
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
            ObservationAnswered = true
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
                                    Id = $"wamid.gm-{Interlocked.Increment(ref _msgCounter)}",
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
