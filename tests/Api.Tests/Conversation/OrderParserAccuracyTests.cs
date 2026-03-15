using FluentAssertions;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Application.Services;

namespace WhatsAppSaaS.Api.Tests.Conversation;

/// <summary>
/// Tests for the upgraded order parser: multi-word plural normalization,
/// noise stripping, ambiguity detection, quantity patterns, and the
/// specific real-world bug cases observed in production.
/// </summary>
public class OrderParserAccuracyTests
{
    public OrderParserAccuracyTests()
    {
        WebhookProcessor.ActiveCatalog = TestCatalogHelper.MenuCatalogWithExtras;
    }

    // ═══════════════════════════════════════════════════════════
    //  CASE A — The original production bug
    //  "voy a querer 2 perros especiales, 2 papas pequeñas y 1 cocacola"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void CaseA_PerrosEspeciales_PapasPequenas_Cocacola()
    {
        var input = "voy a querer 2 perros especiales, 2 papas pequeñas y 1 cocacola";
        var parsed = WebhookProcessor.ParseOrderText(input);

        parsed.Should().HaveCount(3);

        parsed[0].Name.Should().Be("Perro Especial");
        parsed[0].Quantity.Should().Be(2);

        parsed[1].Name.Should().Be("Papas Pequenas");
        parsed[1].Quantity.Should().Be(2);

        parsed[2].Name.Should().Be("Coca Cola");
        parsed[2].Quantity.Should().Be(1);
    }

    // ═══════════════════════════════════════════════════════════
    //  CASE B — Hamburguesas clasicas with cocas
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void CaseB_HamburguesasClasicas_PapaMediana_Cocas()
    {
        var input = "quiero 2 hamburguesas clasicas, 1 papa mediana y 2 cocas";
        var parsed = WebhookProcessor.ParseOrderText(input);

        parsed.Should().HaveCountGreaterOrEqualTo(3);

        var hamb = parsed.FirstOrDefault(p => p.Name.Contains("Hamburguesa"));
        hamb.Should().NotBeNull();
        hamb!.Name.Should().Be("Hamburguesa Clasica");
        hamb.Quantity.Should().Be(2);

        var papa = parsed.FirstOrDefault(p => p.Name.Contains("Papa"));
        papa.Should().NotBeNull();
        // "papa mediana" → Papas Medianas
        papa!.Quantity.Should().Be(1);

        var coca = parsed.FirstOrDefault(p => p.Name.Contains("Coca"));
        coca.Should().NotBeNull();
        coca!.Name.Should().Be("Coca Cola");
        coca!.Quantity.Should().Be(2);
    }

    // ═══════════════════════════════════════════════════════════
    //  CASE C — Ambiguous "dos perros" (clasico or especial?)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void CaseC_DosPerros_ShouldMatchPerroClasicoBecauseAliasExists()
    {
        // "perros" is an alias for Perro Clasico in the demo catalog,
        // so it should match directly without ambiguity.
        var input = "me das dos perros y una coca";
        var parsed = WebhookProcessor.ParseOrderText(input);

        parsed.Should().HaveCountGreaterOrEqualTo(2);

        var perro = parsed.FirstOrDefault(p => p.Name.Contains("Perro"));
        perro.Should().NotBeNull();
        perro!.Name.Should().Be("Perro Clasico");
        perro.Quantity.Should().Be(2);

        var coca = parsed.FirstOrDefault(p => p.Name.Contains("Coca"));
        coca.Should().NotBeNull();
        coca!.Quantity.Should().Be(1);
    }

    // ═══════════════════════════════════════════════════════════
    //  CASE D — Item with observation
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void CaseD_HamburguesaDobleSinCebolla_PapasWithObservation()
    {
        var input = "1 hamburguesa doble sin cebolla y 2 papas";
        var parsed = WebhookProcessor.ParseOrderText(input);

        parsed.Should().HaveCountGreaterOrEqualTo(2);

        var hamb = parsed.FirstOrDefault(p => p.Name.Contains("Doble"));
        hamb.Should().NotBeNull();
        hamb!.Name.Should().Be("Hamburguesa Doble");
        hamb.Quantity.Should().Be(1);
        hamb.Modifiers.Should().Contain("sin cebolla");

        var papa = parsed.FirstOrDefault(p => p.Name.Contains("Papa"));
        papa.Should().NotBeNull();
        papa!.Quantity.Should().Be(2);
    }

    // ═══════════════════════════════════════════════════════════
    //  CASE E — Greeting noise with slang
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void CaseE_GreetingNoise_StillParsesCorrectly()
    {
        var input = "hola bro quisiera 3 cocas y una papa mediana";
        var parsed = WebhookProcessor.ParseOrderText(input);

        parsed.Should().HaveCountGreaterOrEqualTo(2);

        var coca = parsed.FirstOrDefault(p => p.Name.Contains("Coca"));
        coca.Should().NotBeNull();
        coca!.Quantity.Should().Be(3);

        var papa = parsed.FirstOrDefault(p => p.Name.Contains("Papa"));
        papa.Should().NotBeNull();
        papa!.Quantity.Should().Be(1);
    }

    // ═══════════════════════════════════════════════════════════
    //  CASE F — Ambiguous bare word
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void CaseF_BareEspecial_MatchesHamburguesaEspecialViaAlias()
    {
        // "especial" is an alias for Hamburguesa Especial in the demo catalog
        var input = "2 especiales";
        var parsed = WebhookProcessor.ParseOrderText(input);

        parsed.Should().HaveCount(1);
        // Should match via alias (or singularized "especial")
        parsed[0].Name.Should().Be("Hamburguesa Especial");
        parsed[0].Quantity.Should().Be(2);
    }

    // ═══════════════════════════════════════════════════════════
    //  SINGULARIZATION UNIT TESTS
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("perros", "perro")]
    [InlineData("especiales", "especial")]
    [InlineData("clasicas", "clasica")]
    [InlineData("hamburguesas", "hamburguesa")]
    [InlineData("papas", "papa")]
    [InlineData("refrescos", "refresco")]
    [InlineData("cocas", "coca")]
    [InlineData("medianas", "mediana")]
    [InlineData("pequenas", "pequena")]
    [InlineData("grandes", "grande")]
    [InlineData("mixtas", "mixta")]
    [InlineData("calientes", "caliente")]
    public void SingularizeWord_ProducesCorrectSingular(string plural, string expectedSingular)
    {
        WebhookProcessor.SingularizeWord(plural).Should().Be(expectedSingular);
    }

    [Theory]
    [InlineData("perros especiales", "perro especial")]
    [InlineData("hamburguesas clasicas", "hamburguesa clasica")]
    [InlineData("papas pequenas", "papa pequena")]
    [InlineData("papas medianas", "papa mediana")]
    [InlineData("perros calientes", "perro caliente")]
    public void SingularizeWords_ProducesCorrectPhrase(string plural, string expectedSingular)
    {
        WebhookProcessor.SingularizeWords(plural).Should().Be(expectedSingular);
    }

    // ═══════════════════════════════════════════════════════════
    //  NORMALIZE MENU ITEM NAME — multi-word plural resolution
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("perros especiales", "Perro Especial")]
    [InlineData("perro especial", "Perro Especial")]
    [InlineData("hamburguesas clasicas", "Hamburguesa Clasica")]
    [InlineData("papas pequenas", "Papas Pequenas")]
    [InlineData("papas medianas", "Papas Medianas")]
    [InlineData("cocacola", "Coca Cola")]
    [InlineData("coca", "Coca Cola")]
    [InlineData("cocas", "Coca Cola")]
    [InlineData("perro clasico", "Perro Clasico")]
    [InlineData("perros clasicos", "Perro Clasico")]
    public void NormalizeMenuItemName_ResolvesPluralsCorrectly(string input, string expected)
    {
        var result = WebhookProcessor.NormalizeMenuItemName(input, TestCatalogHelper.MenuCatalogWithExtras);
        result.Should().Be(expected);
    }

    // ═══════════════════════════════════════════════════════════
    //  NOISE STRIPPING
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("voy a querer 2 perros especiales", "Perro Especial", 2)]
    [InlineData("me das 3 cocas", "Coca Cola", 3)]
    [InlineData("dame 1 hamburguesa clasica", "Hamburguesa Clasica", 1)]
    [InlineData("ponme 2 papas medianas", "Papas Medianas", 2)]
    [InlineData("regalame una coca", "Coca Cola", 1)]
    [InlineData("quisiera 2 perros especiales", "Perro Especial", 2)]
    public void ParseOrderText_StripsNoisePrefixes(string input, string expectedItem, int expectedQty)
    {
        var parsed = WebhookProcessor.ParseOrderText(input);

        parsed.Should().HaveCountGreaterOrEqualTo(1);
        var item = parsed.First(p => p.Name == expectedItem);
        item.Quantity.Should().Be(expectedQty);
    }

    // ═══════════════════════════════════════════════════════════
    //  QUANTITY PATTERNS
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("dos perros especiales", "Perro Especial", 2)]
    [InlineData("una coca", "Coca Cola", 1)]
    [InlineData("tres hamburguesas clasicas", "Hamburguesa Clasica", 3)]
    [InlineData("2 de perro especial", "Perro Especial", 2)]
    public void ParseOrderText_HandlesVariousQuantityFormats(string input, string expectedItem, int expectedQty)
    {
        var parsed = WebhookProcessor.ParseOrderText(input);

        parsed.Should().HaveCountGreaterOrEqualTo(1);
        var item = parsed.FirstOrDefault(p => p.Name == expectedItem);
        item.Should().NotBeNull($"Expected to find '{expectedItem}' in parsed items: [{string.Join(", ", parsed.Select(p => $"{p.Quantity}x {p.Name}"))}]");
        item!.Quantity.Should().Be(expectedQty);
    }

    // ═══════════════════════════════════════════════════════════
    //  AMBIGUITY DETECTION
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void FindMatchWithConfidence_ExactMatch_ReturnsHighConfidence()
    {
        var catalog = TestCatalogHelper.MenuCatalogWithExtras;
        var (match, confidence, alternatives) = WebhookProcessor.FindMatchWithConfidence("perro especial", catalog);

        match.Should().Be("Perro Especial");
        confidence.Should().Be(1.0);
        alternatives.Should().BeNull();
    }

    [Fact]
    public void FindMatchWithConfidence_NoMatch_ReturnsNull()
    {
        var catalog = TestCatalogHelper.MenuCatalogWithExtras;
        var (match, confidence, alternatives) = WebhookProcessor.FindMatchWithConfidence("pizza margarita", catalog);

        match.Should().BeNull();
        confidence.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════
    //  CLARIFICATION MESSAGE BUILDING
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void BuildAmbiguityClarificationMessage_FormatsCorrectly()
    {
        var items = new List<AmbiguousItemEntry>
        {
            new()
            {
                OriginalText = "especial",
                Quantity = 2,
                Candidates = new List<string> { "Hamburguesa Especial", "Perro Especial" }
            }
        };

        var msg = WebhookProcessor.BuildAmbiguityClarificationMessage(items);

        msg.Should().Contain("2x \"especial\"");
        msg.Should().Contain("1. Hamburguesa Especial");
        msg.Should().Contain("2. Perro Especial");
    }

    // ═══════════════════════════════════════════════════════════
    //  AMBIGUITY RESOLUTION
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void TryResolveAmbiguity_NumericResponse_ResolvesCorrectly()
    {
        var state = new ConversationFields
        {
            PendingAmbiguousItems = new List<AmbiguousItemEntry>
            {
                new()
                {
                    OriginalText = "especial",
                    Quantity = 2,
                    Candidates = new List<string> { "Hamburguesa Especial", "Perro Especial" }
                }
            }
        };

        var result = WebhookProcessor.TryResolveAmbiguity(state, "2");

        result.Should().BeTrue();
        state.Items.Should().HaveCount(1);
        state.Items[0].Name.Should().Be("Perro Especial");
        state.Items[0].Quantity.Should().Be(2);
        state.PendingAmbiguousItems.Should().BeNull();
    }

    [Fact]
    public void TryResolveAmbiguity_TextResponse_ResolvesCorrectly()
    {
        var state = new ConversationFields
        {
            PendingAmbiguousItems = new List<AmbiguousItemEntry>
            {
                new()
                {
                    OriginalText = "especial",
                    Quantity = 2,
                    Candidates = new List<string> { "Hamburguesa Especial", "Perro Especial" }
                }
            }
        };

        var result = WebhookProcessor.TryResolveAmbiguity(state, "perro especial");

        result.Should().BeTrue();
        state.Items.Should().HaveCount(1);
        state.Items[0].Name.Should().Be("Perro Especial");
        state.Items[0].Quantity.Should().Be(2);
        state.PendingAmbiguousItems.Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════
    //  FULL QUICK ORDER FLOW
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void TryParseQuickOrder_ComplexRealOrder_AllItemsCorrect()
    {
        var success = WebhookProcessor.TryParseQuickOrder(
            "voy a querer 2 perros especiales, 2 papas pequeñas y 1 cocacola",
            out var items, out var delivery, out var obs);

        success.Should().BeTrue();
        items.Should().HaveCount(3);

        items.Should().Contain(i => i.Name == "Perro Especial" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Papas Pequenas" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    [Fact]
    public void TryParseQuickOrder_WithDeliveryType_Extracted()
    {
        var success = WebhookProcessor.TryParseQuickOrder(
            "quiero 1 hamburguesa clasica para delivery",
            out var items, out var delivery, out var obs);

        success.Should().BeTrue();
        delivery.Should().Be("delivery");
        items.Should().HaveCount(1);
        items[0].Name.Should().Be("Hamburguesa Clasica");
    }

    // ═══════════════════════════════════════════════════════════
    //  EDGE CASES
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ParseOrderText_MixedPluralAndSingular()
    {
        var input = "1 perro especial y 2 hamburguesas clasicas";
        var parsed = WebhookProcessor.ParseOrderText(input);

        parsed.Should().HaveCountGreaterOrEqualTo(2);

        var perro = parsed.First(p => p.Name.Contains("Perro"));
        perro.Name.Should().Be("Perro Especial");
        perro.Quantity.Should().Be(1);

        var hamb = parsed.First(p => p.Name.Contains("Hamburguesa"));
        hamb.Name.Should().Be("Hamburguesa Clasica");
        hamb.Quantity.Should().Be(2);
    }

    [Fact]
    public void ParseOrderText_WithAccents_StillMatches()
    {
        var input = "2 papas pequeñas y 1 coca cola";
        var parsed = WebhookProcessor.ParseOrderText(input);

        parsed.Should().HaveCountGreaterOrEqualTo(2);

        var papa = parsed.First(p => p.Name.Contains("Papa"));
        papa.Name.Should().Be("Papas Pequenas");
        papa.Quantity.Should().Be(2);
    }

    [Fact]
    public void ParseOrderText_CocacolaJoinedWord_MatchesCocaCola()
    {
        var parsed = WebhookProcessor.ParseOrderText("1 cocacola");

        parsed.Should().HaveCount(1);
        parsed[0].Name.Should().Be("Coca Cola");
        parsed[0].Quantity.Should().Be(1);
    }

    [Fact]
    public void ParseOrderText_PerroConQueso_MatchesSingleItem()
    {
        var parsed = WebhookProcessor.ParseOrderText("1 perro con queso");

        parsed.Should().HaveCount(1);
        parsed[0].Name.Should().Be("Perro con Queso");
    }

    [Fact]
    public void ParseOrderText_MultilineOrder_AllItemsParsed()
    {
        var input = "2 perros especiales\n2 papas pequeñas\n1 coca cola";
        var parsed = WebhookProcessor.ParseOrderText(input);

        parsed.Should().HaveCount(3);
        parsed[0].Name.Should().Be("Perro Especial");
        parsed[0].Quantity.Should().Be(2);
        parsed[1].Name.Should().Be("Papas Pequenas");
        parsed[1].Quantity.Should().Be(2);
        parsed[2].Name.Should().Be("Coca Cola");
        parsed[2].Quantity.Should().Be(1);
    }
}
