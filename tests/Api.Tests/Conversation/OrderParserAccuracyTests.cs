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

    // ═══════════════════════════════════════════════════════════
    //  CHAINED QUANTITY+ITEM GROUPS (no commas)
    //  Real-world LatAm typing pattern: users write multiple
    //  items in one line separated only by whitespace.
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ParseOrderText_ChainedNoCommas_ProductionBug()
    {
        // THE EXACT failing production input
        var input = "voy a querer 2 perros especiales 2 papas pequeñas y una cocacola";
        var parsed = WebhookProcessor.ParseOrderText(input);

        parsed.Should().HaveCount(3);
        parsed.Should().Contain(p => p.Name == "Perro Especial" && p.Quantity == 2);
        parsed.Should().Contain(p => p.Name == "Papas Pequenas" && p.Quantity == 2);
        parsed.Should().Contain(p => p.Name == "Coca Cola" && p.Quantity == 1);
    }

    [Fact]
    public void ParseOrderText_ChainedNoCommas_NoConjunction()
    {
        // No "y" at all — purely whitespace-separated chained groups
        var input = "voy a querer 2 perros especiales 2 papas pequeñas 1 cocacola";
        var parsed = WebhookProcessor.ParseOrderText(input);

        parsed.Should().HaveCount(3);
        parsed.Should().Contain(p => p.Name == "Perro Especial" && p.Quantity == 2);
        parsed.Should().Contain(p => p.Name == "Papas Pequenas" && p.Quantity == 2);
        parsed.Should().Contain(p => p.Name == "Coca Cola" && p.Quantity == 1);
    }

    [Fact]
    public void ParseOrderText_ChainedNoCommas_ThreeItems()
    {
        var input = "quisiera 1 hamburguesa clasica 1 papa mediana 2 cocas";
        var parsed = WebhookProcessor.ParseOrderText(input);

        parsed.Should().HaveCount(3);
        parsed.Should().Contain(p => p.Name == "Hamburguesa Clasica" && p.Quantity == 1);
        parsed.Should().Contain(p => p.Name == "Papas Medianas" && p.Quantity == 1);
        parsed.Should().Contain(p => p.Name == "Coca Cola" && p.Quantity == 2);
    }

    [Fact]
    public void ParseOrderText_ChainedWithConjunction()
    {
        var input = "ponme 2 perros especiales y 2 papas pequeñas";
        var parsed = WebhookProcessor.ParseOrderText(input);

        parsed.Should().HaveCount(2);
        parsed.Should().Contain(p => p.Name == "Perro Especial" && p.Quantity == 2);
        parsed.Should().Contain(p => p.Name == "Papas Pequenas" && p.Quantity == 2);
    }

    [Fact]
    public void ParseOrderText_ChainedAllDigitQuantities()
    {
        var input = "2 perros especiales 2 papas pequeñas 2 cocas";
        var parsed = WebhookProcessor.ParseOrderText(input);

        parsed.Should().HaveCount(3);
        parsed.Should().Contain(p => p.Name == "Perro Especial" && p.Quantity == 2);
        parsed.Should().Contain(p => p.Name == "Papas Pequenas" && p.Quantity == 2);
        parsed.Should().Contain(p => p.Name == "Coca Cola" && p.Quantity == 2);
    }

    [Fact]
    public void ParseOrderText_ChainedWithWordNumbers()
    {
        // Mix of digit and word quantities, no commas
        var input = "2 perros especiales una coca";
        var parsed = WebhookProcessor.ParseOrderText(input);

        parsed.Should().HaveCount(2);
        parsed.Should().Contain(p => p.Name == "Perro Especial" && p.Quantity == 2);
        parsed.Should().Contain(p => p.Name == "Coca Cola" && p.Quantity == 1);
    }

    [Fact]
    public void ParseOrderText_ChainedWithWordNumbersOnly()
    {
        var input = "dos hamburguesas clasicas una coca";
        var parsed = WebhookProcessor.ParseOrderText(input);

        parsed.Should().HaveCount(2);
        parsed.Should().Contain(p => p.Name == "Hamburguesa Clasica" && p.Quantity == 2);
        parsed.Should().Contain(p => p.Name == "Coca Cola" && p.Quantity == 1);
    }

    // ═══════════════════════════════════════════════════════════
    //  SplitChainedQuantities — unit tests
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("2 perros especiales 2 papas pequenas", new[] { "2 perros especiales", "2 papas pequenas" })]
    [InlineData("2 perros especiales 2 papas pequenas 1 cocacola", new[] { "2 perros especiales", "2 papas pequenas", "1 cocacola" })]
    [InlineData("1 hamburguesa clasica 1 papa mediana 2 cocas", new[] { "1 hamburguesa clasica", "1 papa mediana", "2 cocas" })]
    [InlineData("2 perros especiales una coca", new[] { "2 perros especiales", "una coca" })]
    [InlineData("combo 2", new[] { "combo 2" })]  // should NOT split — digit at end, no space+letter after
    [InlineData("coca cola 355", new[] { "coca cola 355" })]  // should NOT split
    [InlineData("1 hamburguesa", new[] { "1 hamburguesa" })]  // single item, no split
    public void SplitChainedQuantities_SplitsCorrectly(string input, string[] expected)
    {
        var result = WebhookProcessor.SplitChainedQuantities(input);
        result.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
    }

    // ═══════════════════════════════════════════════════════════
    //  TryParseQuickOrder — chained input variant
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void TryParseQuickOrder_ChainedNoCommas_AllItemsParsed()
    {
        var success = WebhookProcessor.TryParseQuickOrder(
            "voy a querer 2 perros especiales 2 papas pequeñas y una cocacola",
            out var items, out _, out _);

        success.Should().BeTrue();
        items.Should().HaveCount(3);
        items.Should().Contain(i => i.Name == "Perro Especial" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Papas Pequenas" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    [Fact]
    public void TryParseQuickOrder_ChainedNoDelimiters_AllItemsParsed()
    {
        var success = WebhookProcessor.TryParseQuickOrder(
            "2 perros especiales 2 papas pequeñas 2 cocas",
            out var items, out _, out _);

        success.Should().BeTrue();
        items.Should().HaveCount(3);
        items.Should().Contain(i => i.Name == "Perro Especial" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Papas Pequenas" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 2);
    }

    // ═══════════════════════════════════════════════════════════
    //  ORDERING INTENT + ITEMS IN SAME MESSAGE
    //  Must parse items, not fall back to "escríbeme tu pedido"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void TryParseQuickOrder_Quisiera_WithItems_ParsesOrder()
    {
        var success = WebhookProcessor.TryParseQuickOrder(
            "quisiera 1 hamburguesa clasica 1 papa mediana 2 cocas",
            out var items, out _, out _);

        success.Should().BeTrue();
        items.Should().HaveCount(3);
        items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 1);
        items.Should().Contain(i => i.Name == "Papas Medianas" && i.Quantity == 1);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 2);
    }

    [Fact]
    public void TryParseQuickOrder_Quiero_WithItems_ParsesOrder()
    {
        var success = WebhookProcessor.TryParseQuickOrder(
            "quiero 2 hamburguesas clasicas y 1 coca",
            out var items, out _, out _);

        success.Should().BeTrue();
        items.Should().HaveCount(2);
        items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    [Fact]
    public void TryParseQuickOrder_VoyAQuerer_WithChainedItems_ParsesOrder()
    {
        var success = WebhookProcessor.TryParseQuickOrder(
            "voy a querer 2 perros especiales 2 papas pequeñas 1 cocacola",
            out var items, out _, out _);

        success.Should().BeTrue();
        items.Should().HaveCount(3);
        items.Should().Contain(i => i.Name == "Perro Especial" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Papas Pequenas" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    [Fact]
    public void TryParseQuickOrder_Ponme_WithItems_ParsesOrder()
    {
        var success = WebhookProcessor.TryParseQuickOrder(
            "ponme 1 perro especial y 1 coca",
            out var items, out _, out _);

        success.Should().BeTrue();
        items.Should().HaveCount(2);
        items.Should().Contain(i => i.Name == "Perro Especial" && i.Quantity == 1);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    [Fact]
    public void TryParseQuickOrder_Regalame_WithItems_ParsesOrder()
    {
        var success = WebhookProcessor.TryParseQuickOrder(
            "regalame 2 papas medianas",
            out var items, out _, out _);

        success.Should().BeTrue();
        items.Should().HaveCount(1);
        items.Should().Contain(i => i.Name == "Papas Medianas" && i.Quantity == 2);
    }

    // ═══════════════════════════════════════════════════════════
    //  IsOrderingIntent — confirms these trigger ordering intent
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("quisiera 1 hamburguesa clasica 1 papa mediana 2 cocas")]
    [InlineData("quisiera pedir algo")]
    public void IsOrderingIntent_ReturnsTrue_ForQuisiera(string input)
    {
        var t = input.Trim().ToLowerInvariant();
        WebhookProcessor.IsOrderingIntent(t).Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════
    //  CANCEL COMMAND — typo tolerance
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("cancelar", true)]
    [InlineData("cancelar pedido", true)]
    [InlineData("cancelar orden", true)]
    [InlineData("cancela", true)]
    [InlineData("cancelo", true)]
    [InlineData("cancelae", true)]      // common typo
    [InlineData("cancalar", true)]      // transposed vowel
    [InlineData("cancela pedido", true)]
    [InlineData("cancelo pedido", true)]
    [InlineData("borrar todo", true)]
    [InlineData("empezar de cero", true)]
    [InlineData("hamburguesa", false)]  // should not match
    [InlineData("hola", false)]         // should not match
    [InlineData("canal", false)]        // too short / too different
    public void IsCancelCommand_MatchesTypos(string input, bool expected)
    {
        WebhookProcessor.IsCancelCommand(input).Should().Be(expected);
    }

    // ═══════════════════════════════════════════════════════════
    //  ORDERING PHRASE SAFETY — must not parse as menu items
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("quisiera hacer un pedido")]
    [InlineData("quiero hacer un pedido")]
    [InlineData("quiero pedir")]
    public void ParseOrderText_PureOrderingPhrase_ReturnsZeroItems(string input)
    {
        var parsed = WebhookProcessor.ParseOrderText(input);
        parsed.Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Should().BeEmpty($"'{input}' should not match any menu items");
    }

    // ═══════════════════════════════════════════════════════════
    //  REGRESSION — "me vas a dar" natural inline multi-item
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Regression_MeVasADar_MultiItem_AllParsed()
    {
        var input = "me vas a dar 3 perros clasicos 2 papas grandes y una coacola";
        var parsed = WebhookProcessor.ParseOrderText(input);
        var items = parsed.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();

        items.Should().HaveCount(3, "all three items must be parsed");
        items.Should().Contain(p => p.Name == "Perro Clasico" && p.Quantity == 3);
        items.Should().Contain(p => p.Name == "Papas Grandes" && p.Quantity == 2);
        items.Should().Contain(p => p.Name == "Coca Cola" && p.Quantity == 1);
    }

    [Fact]
    public void Regression_Dame_MultiItem()
    {
        var input = "dame 2 hamburguesas clasicas y 1 coca cola";
        var parsed = WebhookProcessor.ParseOrderText(input);
        var items = parsed.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();

        items.Should().HaveCount(2);
        items.Should().Contain(p => p.Name == "Hamburguesa Clasica" && p.Quantity == 2);
        items.Should().Contain(p => p.Name == "Coca Cola" && p.Quantity == 1);
    }

    [Fact]
    public void Regression_Ponme_WithCommas()
    {
        var input = "ponme 2 perros clasicos, 2 papas grandes y 1 coca cola";
        var parsed = WebhookProcessor.ParseOrderText(input);
        var items = parsed.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();

        items.Should().HaveCount(3);
        items.Should().Contain(p => p.Name == "Perro Clasico" && p.Quantity == 2);
        items.Should().Contain(p => p.Name == "Papas Grandes" && p.Quantity == 2);
        items.Should().Contain(p => p.Name == "Coca Cola" && p.Quantity == 1);
    }

    [Fact]
    public void Regression_TypoTolerance_Coacola()
    {
        var input = "1 coacola";
        var parsed = WebhookProcessor.ParseOrderText(input);
        var items = parsed.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();

        items.Should().ContainSingle();
        items[0].Name.Should().Be("Coca Cola");
    }

    [Fact]
    public void Regression_TypoTolerance_Clasicos()
    {
        var input = "3 perros clasicos";
        var parsed = WebhookProcessor.ParseOrderText(input);
        var items = parsed.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();

        items.Should().ContainSingle();
        items[0].Name.Should().Be("Perro Clasico");
        items[0].Quantity.Should().Be(3);
    }

    [Fact]
    public void Regression_MenuPrices_NotNonsense()
    {
        var catalog = WebhookProcessor.MenuCatalog;
        var cocaCola = catalog.FirstOrDefault(e => e.Canonical == "Coca Cola");
        cocaCola.Should().NotBeNull();
        cocaCola!.Price.Should().BeGreaterThan(0.50m, "Coca Cola price should not be nonsense like $0.02");
    }

    [Fact]
    public void TryParseQuickOrder_MultiItem_AllItems()
    {
        var ok = WebhookProcessor.TryParseQuickOrder(
            "me vas a dar 3 perros clasicos 2 papas grandes y una coacola",
            out var items, out _, out _);

        ok.Should().BeTrue();
        items.Should().HaveCount(3);
        items.Should().Contain(i => i.Name == "Perro Clasico" && i.Quantity == 3);
        items.Should().Contain(i => i.Name == "Papas Grandes" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }
}
