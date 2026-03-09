using FluentAssertions;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Application.Services;

namespace WhatsAppSaaS.Api.Tests.Conversation;

/// <summary>
/// Conversational stress tests for the order parser.
/// Each test simulates a real WhatsApp message from a Venezuelan user and
/// verifies: parsed items, quantities, delivery detection, observations.
///
/// This file does NOT modify any production code.
/// Tests that fail reveal parser gaps to fix in a follow-up commit.
/// </summary>
public class ParserConversationalStressTests
{
    public ParserConversationalStressTests()
    {
        // Ensure static catalog is set for NormalizeMenuItemName calls
        WebhookProcessor.ActiveCatalog = TestCatalogHelper.MenuCatalogWithExtras;
    }

    // ─────────────────────────────────────────────────────────────
    // Case 1 — "epa bro dame 2 hamburguesas"
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void Case1_EpaBroDame2Hamburguesas()
    {
        var parsed = Parse("epa bro dame 2 hamburguesas");

        parsed.Items.Should().ContainSingle();
        parsed.Items[0].Name.Should().Be("Hamburguesa Clasica");
        parsed.Items[0].Quantity.Should().Be(2);
    }

    // ─────────────────────────────────────────────────────────────
    // Case 2 — Multi-line: "2 hamburguesas\n1 coca"
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void Case2_MultiLine_2Hamburguesas_1Coca()
    {
        var parsed = Parse("2 hamburguesas\n1 coca");

        parsed.Items.Should().HaveCount(2);
        parsed.Items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        parsed.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    // ─────────────────────────────────────────────────────────────
    // Case 3 — "una hamburguesa doble sin cebolla"
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void Case3_HamburguesaDobleSinCebolla()
    {
        var parsed = WebhookProcessor.ParseOrderText("una hamburguesa doble sin cebolla");

        parsed.Should().ContainSingle();
        parsed[0].Name.Should().Be("Hamburguesa Doble");
        parsed[0].Quantity.Should().Be(1);
        parsed[0].Modifiers.Should().Contain("sin cebolla");
    }

    // ─────────────────────────────────────────────────────────────
    // Case 4 — Multi-line with greeting + delivery
    //   "epa bro\n2 hamburguesas\n1 coca\ndelivery"
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void Case4_MultiLineWithGreetingAndDelivery()
    {
        var text = "epa bro\n2 hamburguesas\n1 coca\ndelivery";

        WebhookProcessor.TryParseQuickOrder(text, out var items, out var dt, out _);

        dt.Should().Be("delivery");
        items.Should().HaveCount(2);
        items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    // ─────────────────────────────────────────────────────────────
    // Case 5 — Edit intent: "agrega una coca"
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void Case5_AgregaUnaCoca_EditDetected()
    {
        var result = WebhookProcessor.TryParseOrderModification("agrega una coca", out var mod);

        // "agrega" + word-number "una" → pattern expects digit after "agrega"
        // If this fails, it means the modification parser doesn't handle word-numbers.
        // Documenting expected ideal behavior:
        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.Add);
        mod.ItemName.Should().Be("Coca Cola");
        mod.Quantity.Should().Be(1);
    }

    // ─────────────────────────────────────────────────────────────
    // Case 6 — Swap: "cambia la hamburguesa por una doble"
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void Case6_CambiaHamburguesaPorDoble_SwapDetected()
    {
        var result = WebhookProcessor.TryParseOrderModification(
            "cambia la hamburguesa por una doble", out var mod);

        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.Swap);
        mod.ItemName.Should().Be("Hamburguesa Clasica");
        mod.SwapTargetName.Should().Be("Hamburguesa Doble");
    }

    // ─────────────────────────────────────────────────────────────
    // Case 7 — Venezuelan slang: "mano dame una doble"
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void Case7_ManoDameUnaDoble()
    {
        // "mano" is VE slang for "hermano" / bro.
        // "dame" is in the noise list but "mano" is not.
        // The parser should still find "doble" → Hamburguesa Doble.
        var parsed = Parse("mano dame una doble");

        parsed.Items.Should().ContainSingle();
        parsed.Items[0].Name.Should().Be("Hamburguesa Doble");
        parsed.Items[0].Quantity.Should().Be(1);
    }

    // ─────────────────────────────────────────────────────────────
    // Case 8 — "2 hamburguesas bien tostadas"
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void Case8_2HamburguesasBienTostadas()
    {
        var parsed = WebhookProcessor.ParseOrderText("2 hamburguesas bien tostadas");

        parsed.Should().ContainSingle();
        parsed[0].Name.Should().Be("Hamburguesa Clasica");
        parsed[0].Quantity.Should().Be(2);
        parsed[0].Modifiers.Should().Contain("bien tostada");
    }

    // ─────────────────────────────────────────────────────────────
    // Case 9 — "quiero 2 hamburguesas y 1 coca"
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void Case9_Quiero2HamburguesasY1Coca()
    {
        var parsed = Parse("quiero 2 hamburguesas y 1 coca");

        parsed.Items.Should().HaveCount(2);
        parsed.Items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        parsed.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    // ─────────────────────────────────────────────────────────────
    // Case 10 — "una clásica y delivery"
    // ─────────────────────────────────────────────────────────────

    [Fact]
    public void Case10_UnaClasicaYDelivery()
    {
        WebhookProcessor.TryParseQuickOrder("una clásica y delivery", out var items, out var dt, out _);

        dt.Should().Be("delivery");
        items.Should().ContainSingle();
        items[0].Name.Should().Be("Hamburguesa Clasica");
        items[0].Quantity.Should().Be(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //   EXTENDED CASES — Venezuelan WhatsApp patterns
    // ═══════════════════════════════════════════════════════════════

    // ── Slang filler words that are NOT in the noise pattern ──

    [Theory]
    [InlineData("bro dame 1 hamburguesa")]
    [InlineData("pana dame 1 hamburguesa")]
    [InlineData("vale dame 1 hamburguesa")]
    [InlineData("mano 1 hamburguesa")]
    [InlineData("loco dame 1 hamburguesa")]
    public void SlangFiller_StillParsesItem(string input)
    {
        var parsed = Parse(input);
        parsed.Items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 1,
            $"parser should find Hamburguesa Clasica in: \"{input}\"");
    }

    // ── Compound orders with "y" conjunction ──

    [Fact]
    public void Compound_3ItemsWithY()
    {
        var parsed = Parse("2 hamburguesas, 1 perro caliente y 3 cocas");

        parsed.Items.Should().HaveCount(3);
        parsed.Items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        parsed.Items.Should().Contain(i => i.Name == "Perro Clasico" && i.Quantity == 1);
        parsed.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 3);
    }

    // ── "con" as item link vs modifier ──

    [Fact]
    public void ConAsItemLink_HamburguesaConPapas()
    {
        var parsed = Parse("2 hamburguesas con papas");

        // "con papas" → "papas" is a known menu item → should split into 2 segments
        parsed.Items.Should().HaveCount(2);
        parsed.Items.Should().Contain(i => i.Name == "Hamburguesa Clasica");
        parsed.Items.Should().Contain(i => i.Name == "Papas Medianas");
    }

    [Fact]
    public void ConAsModifier_HamburguesaConExtraQueso()
    {
        var parsed = WebhookProcessor.ParseOrderText("1 hamburguesa con extra queso");

        // "con extra queso" is a modifier, NOT an item link
        parsed.Should().ContainSingle();
        parsed[0].Name.Should().Be("Hamburguesa Clasica");
        parsed[0].Modifiers.Should().Contain("extra queso");
    }

    // ── Delivery phrase variations ──

    [Theory]
    [InlineData("2 perros a domicilio", "delivery")]
    [InlineData("1 hamburguesa para retirar", "pickup")]
    [InlineData("3 cocas voy a buscar", "pickup")]
    [InlineData("2 combos pickup", "pickup")]
    [InlineData("1 perro caliente envío", "delivery")]
    public void DeliveryVariations(string input, string expected)
    {
        WebhookProcessor.TryParseQuickOrder(input, out _, out var dt, out _);
        dt.Should().Be(expected);
    }

    // ── Edit patterns ──

    [Theory]
    [InlineData("agrega 2 hamburguesas")]
    [InlineData("agregame 1 coca")]
    [InlineData("sumale 1 papa")]
    [InlineData("ponme 3 cocas")]
    public void EditAdd_Detected(string input)
    {
        WebhookProcessor.TryParseOrderModification(input, out var mod).Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.Add);
        mod.Quantity.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData("quita la coca")]
    [InlineData("elimina las papas")]
    [InlineData("sin las hamburguesas")]
    public void EditRemove_Detected(string input)
    {
        WebhookProcessor.TryParseOrderModification(input, out var mod).Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.Remove);
    }

    // ── Observation / modifier edge cases ──

    [Fact]
    public void Observation_SinTomate()
    {
        var parsed = WebhookProcessor.ParseOrderText("1 hamburguesa sin tomate");
        parsed.Should().ContainSingle();
        parsed[0].Modifiers.Should().Contain("sin tomate");
    }

    [Fact]
    public void Observation_ExtraCarne()
    {
        var parsed = WebhookProcessor.ParseOrderText("1 hamburguesa extra carne");
        parsed.Should().ContainSingle();
        parsed[0].Modifiers.Should().Contain("extra carne");
    }

    [Fact]
    public void Observation_AlPunto()
    {
        var parsed = WebhookProcessor.ParseOrderText("1 hamburguesa al punto");
        parsed.Should().ContainSingle();
        parsed[0].Modifiers.Should().Contain("al punto");
    }

    // ── Word numbers in various positions ──

    [Theory]
    [InlineData("tres hamburguesas", "Hamburguesa Clasica", 3)]
    [InlineData("cinco cocas", "Coca Cola", 5)]
    [InlineData("cuatro perros calientes", "Perro Clasico", 4)]
    public void WordNumbers_ParsedCorrectly(string input, string expectedItem, int expectedQty)
    {
        var parsed = Parse(input);
        parsed.Items.Should().Contain(i => i.Name == expectedItem && i.Quantity == expectedQty);
    }

    // ── Multi-line with leading "y" on new line ──

    [Fact]
    public void MultiLine_LeadingY_OnNewLine()
    {
        // "y" at the start of a line: not preceded by whitespace on that line,
        // so \s+y\s+ regex won't split it. Testing actual parser behavior.
        var text = "1 hamburguesa\ny 1 coca";
        var parsed = WebhookProcessor.ParseOrderText(text);

        // If parser handles leading "y" correctly, expect 2 items.
        // If not, document the gap.
        parsed.Should().HaveCount(2);
        parsed.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 1);
        parsed.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    // ── Swap patterns ──

    [Theory]
    [InlineData("cambia la coca por una pepsi", "Coca Cola", "Pepsi")]
    [InlineData("cambia el perro por una hamburguesa doble", "Perro Clasico", "Hamburguesa Doble")]
    [InlineData("cambia las papas medianas por papas grandes", "Papas Medianas", "Papas Grandes")]
    public void Swap_VariousItems(string input, string expectedFrom, string expectedTo)
    {
        WebhookProcessor.TryParseOrderModification(input, out var mod).Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.Swap);
        mod.ItemName.Should().Be(expectedFrom);
        mod.SwapTargetName.Should().Be(expectedTo);
    }

    // ── "otra" / "otro" references ──

    [Fact]
    public void Otra_HamburguesaDoble()
    {
        var parsed = WebhookProcessor.ParseOrderText("1 hamburguesa y otra hamburguesa doble");
        parsed.Should().HaveCount(2);
        parsed.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 1);
        parsed.Should().Contain(i => i.Name == "Hamburguesa Doble" && i.Quantity == 1);
    }

    // ── Embedded observation with delivery ──

    [Fact]
    public void EmbeddedObservation_WithDelivery()
    {
        WebhookProcessor.TryParseQuickOrder(
            "1 hamburguesa sin cebolla delivery",
            out var items, out var dt, out var obs);

        dt.Should().Be("delivery");
        items.Should().ContainSingle();
        items[0].Name.Should().Be("Hamburguesa Clasica");
        obs.Should().Contain("sin cebolla");
    }

    // ── Heavy realistic order ──

    [Fact]
    public void HeavyOrder_MultiLineRealistic()
    {
        var text = "3 hamburguesas dobles\n2 perros calientes\n5 cocas\n1 papa grande\ndelivery";

        WebhookProcessor.TryParseQuickOrder(text, out var items, out var dt, out _);

        dt.Should().Be("delivery");
        items.Should().HaveCount(4);
        items.Should().Contain(i => i.Name == "Hamburguesa Doble" && i.Quantity == 3);
        items.Should().Contain(i => i.Name == "Perro Clasico" && i.Quantity == 2);
        items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 5);
        items.Should().Contain(i => i.Name == "Papas Grandes" && i.Quantity == 1);
    }

    // ── Typo resilience ──

    [Theory]
    [InlineData("2 hamburgesas y 1 coka", 2)]
    [InlineData("1 amburguesa doble", 1)]
    [InlineData("3 perr calientes", 1)]  // fuzzy may or may not match
    public void Typos_StillParseItems(string input, int minExpectedItems)
    {
        var parsed = Parse(input);
        parsed.Items.Count.Should().BeGreaterThanOrEqualTo(minExpectedItems,
            $"parser should resolve at least {minExpectedItems} items from typos in: \"{input}\"");
    }

    // ── Stall / ambiguous detection ──

    [Theory]
    [InlineData("mmm")]
    [InlineData("no se")]
    [InlineData("espera")]
    [InlineData("que tienen")]
    [InlineData("dejame pensar")]
    public void Stall_DetectedCorrectly(string input)
    {
        var t = WebhookProcessor.Normalize(input);
        WebhookProcessor.IsAmbiguousStall(t).Should().BeTrue();
    }

    [Theory]
    [InlineData("2 hamburguesas")]
    [InlineData("agrega 1 coca")]
    [InlineData("confirmar")]
    [InlineData("cancelar")]
    public void NotStall_RealIntentNotFlagged(string input)
    {
        var t = WebhookProcessor.Normalize(input);
        WebhookProcessor.IsAmbiguousStall(t).Should().BeFalse();
    }

    // ── Edit / Cancel commands ──

    [Theory]
    [InlineData("editar")]
    [InlineData("modificar")]
    [InlineData("cambiar pedido")]
    public void EditCommand_Detected(string input)
    {
        WebhookProcessor.IsEditCommand(input).Should().BeTrue();
    }

    [Theory]
    [InlineData("cancelar")]
    [InlineData("cancelar pedido")]
    [InlineData("borrar todo")]
    [InlineData("empezar de cero")]
    public void CancelCommand_Detected(string input)
    {
        WebhookProcessor.IsCancelCommand(input).Should().BeTrue();
    }

    // ── Venezuelan combo ordering ──

    [Fact]
    public void ComboOrder_ComboClasico()
    {
        var parsed = Parse("1 combo clasico y 1 coca");
        parsed.Items.Should().HaveCount(2);
        parsed.Items.Should().Contain(i => i.Name == "Combo Clasico" && i.Quantity == 1);
        parsed.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    // ── "Dame" + word number without digit ──

    [Fact]
    public void Dame_WordNumber_UnaHamburguesa()
    {
        var parsed = Parse("dame una hamburguesa");
        parsed.Items.Should().ContainSingle();
        parsed.Items[0].Name.Should().Be("Hamburguesa Clasica");
        parsed.Items[0].Quantity.Should().Be(1);
    }

    // ── "Agrega" with word number (edge case for modification parser) ──

    [Fact]
    public void AgregaWordNumber_Dos()
    {
        // "agrega dos cocas" — modification parser expects digit after "agrega"
        var result = WebhookProcessor.TryParseOrderModification("agrega dos cocas", out var mod);
        // This tests whether word-numbers work in the modification parser
        result.Should().BeTrue();
        mod.Type.Should().Be(WebhookProcessor.ModificationType.Add);
        mod.ItemName.Should().Be("Coca Cola");
        mod.Quantity.Should().Be(2);
    }

    // ── Order with accent marks ──

    [Fact]
    public void Accents_ClasicaWithAccent()
    {
        var parsed = Parse("1 hamburguesa clásica");
        parsed.Items.Should().ContainSingle();
        parsed.Items[0].Name.Should().Be("Hamburguesa Clasica");
    }

    // ── Multiple observations on separate items ──

    [Fact]
    public void MultipleObservations_SeparateItems()
    {
        var parsed = WebhookProcessor.ParseOrderText(
            "1 hamburguesa sin cebolla, 1 perro caliente con extra queso");

        parsed.Should().HaveCount(2);

        var h = parsed.FirstOrDefault(i => i.Name == "Hamburguesa Clasica");
        h.Should().NotBeNull();
        h!.Modifiers.Should().Contain("sin cebolla");

        var p = parsed.FirstOrDefault(i => i.Name == "Perro Clasico");
        p.Should().NotBeNull();
        p!.Modifiers.Should().Contain("extra queso");
    }

    // ── Empty / garbage input ──

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("jajaja")]
    [InlineData("xd")]
    public void GarbageInput_NoItemsParsed(string input)
    {
        var parsed = WebhookProcessor.ParseOrderText(input);
        parsed.Should().BeEmpty();
    }

    // ── Single word item match ──

    [Theory]
    [InlineData("hamburguesa", "Hamburguesa Clasica")]
    [InlineData("doble", "Hamburguesa Doble")]
    [InlineData("bacon", "Hamburguesa Bacon")]
    [InlineData("bbq", "Hamburguesa BBQ")]
    [InlineData("perro", "Perro Clasico")]
    [InlineData("coca", "Coca Cola")]
    [InlineData("pepsi", "Pepsi")]
    [InlineData("agua", "Agua")]
    [InlineData("malta", "Malta")]
    public void SingleWord_ResolvesToCanonical(string alias, string expected)
    {
        var resolved = WebhookProcessor.NormalizeMenuItemName(alias);
        resolved.Should().Be(expected);
    }

    // ═══════════════════════════════════════════════════════════════
    //   Helpers
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Convenience wrapper: calls TryParseQuickOrder and returns a result object.
    /// </summary>
    private static ParseResult Parse(string input)
    {
        WebhookProcessor.TryParseQuickOrder(input, out var items, out var dt, out var obs);
        return new ParseResult(items, dt, obs);
    }

    private sealed record ParseResult(
        List<(string Name, int Quantity)> Items,
        string? DeliveryType,
        string? Observation);
}
