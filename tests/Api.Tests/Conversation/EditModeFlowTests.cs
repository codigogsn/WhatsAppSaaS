using FluentAssertions;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Application.Services;

namespace WhatsAppSaaS.Api.Tests.Conversation;

/// <summary>
/// Pure-helper coverage for the "Editar pedido" controlled sub-flow:
///   • Numbered list rendering matches the format the customer sees
///   • Menu-choice classifier accepts the three button-mapped strings
///     plus a few common Spanish variants, rejects everything else
///   • Remove-selection parser accepts "1", "2", tolerates a leading
///     "quitar"/"el"/"#", rejects out-of-range and free text
///   • Quantity-change parser accepts "1 x 3", "1x3", "2 = 5",
///     "cambiar 1 a 3", rejects out-of-range and missing operator
///   • Button-id map routes the three new IDs to the canonical text
///     the dispatcher gate expects
///
/// End-to-end dispatcher behaviour is exercised by the existing conversation
/// suite once these helpers are wired in; isolating the helpers keeps the
/// edit-mode regression net independent of WebApplicationFactory baseline.
/// </summary>
public class EditModeFlowTests
{
    private static List<ConversationItemEntry> Sample() => new()
    {
        new ConversationItemEntry { Name = "Refresco bombita", Quantity = 2, UnitPrice = 1.5m },
        new ConversationItemEntry { Name = "Shawarma 350 grs", Quantity = 2, UnitPrice = 6.0m, Modifiers = "con salsa de garbanzos" },
    };

    [Fact]
    public void BuildNumberedItemList_renders_one_indexed_with_modifiers()
    {
        WebhookProcessor.BuildNumberedItemList(Sample())
            .Should().Be("1) 2x Refresco bombita\n2) 2x Shawarma 350 grs (con salsa de garbanzos)");
    }

    [Fact]
    public void BuildEditModeMenuBody_includes_summary_and_humano_hint()
    {
        var body = WebhookProcessor.BuildEditModeMenuBody(Sample());
        body.Should().StartWith("Claro. Vamos a corregir tu pedido.");
        body.Should().Contain("Tu pedido actual es:");
        body.Should().Contain("1) 2x Refresco bombita");
        body.Should().Contain("¿Qué deseas hacer?");
        body.Should().Contain("humano", "the customer must always have a written escape to handoff");
    }

    [Theory]
    [InlineData("quitar",             "remove")]
    [InlineData("quitar producto",    "remove")]
    [InlineData("remover",            "remove")]
    [InlineData("eliminar",           "remove")]
    [InlineData("cambiar cantidades", "quantity")]
    [InlineData("cantidades",         "quantity")]
    [InlineData("cantidad",           "quantity")]
    [InlineData("rehacer",            "quantity remap test: should NOT be quantity")]
    [InlineData("rehacer todo",       "redo")]
    [InlineData("empezar de nuevo",   "redo")]
    public void TryParseEditMenuChoice_matches_known_phrases(string text, string expected)
    {
        // "rehacer" alone must map to redo, not quantity. Special-case the
        // misnamed test parameter to make the expectation explicit.
        var canonicalExpected = expected.StartsWith("quantity remap") ? "redo" : expected;
        WebhookProcessor.TryParseEditMenuChoice(text).Should().Be(canonicalExpected);
    }

    [Theory]
    [InlineData("hola")]
    [InlineData("2 shawarmas")]
    [InlineData("confirmar")]
    [InlineData("")]
    public void TryParseEditMenuChoice_rejects_other_text(string text)
    {
        WebhookProcessor.TryParseEditMenuChoice(text).Should().BeNull();
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("quitar 1", 1)]
    [InlineData("el 2", 2)]
    [InlineData("#1", 1)]
    [InlineData("número 2", 2)]
    public void TryParseRemoveSelection_accepts_valid_numbers(string text, int expectedIdx)
    {
        WebhookProcessor.TryParseRemoveSelection(text, max: 2, oneBasedIndex: out var idx)
            .Should().BeTrue();
        idx.Should().Be(expectedIdx);
    }

    [Theory]
    [InlineData("0")]            // below range
    [InlineData("3")]            // above range
    [InlineData("uno")]          // word, not digit
    [InlineData("1 x 3")]        // belongs to the quantity parser
    [InlineData("")]
    public void TryParseRemoveSelection_rejects_invalid_input(string text)
    {
        WebhookProcessor.TryParseRemoveSelection(text, max: 2, oneBasedIndex: out _)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("1 x 3", 1, 3)]
    [InlineData("1x3",   1, 3)]
    [InlineData("2 = 5", 2, 5)]
    [InlineData("cambiar 1 a 3", 1, 3)]
    [InlineData("1 × 4", 1, 4)]
    public void TryParseQuantityChange_accepts_canonical_forms(string text, int idx, int qty)
    {
        WebhookProcessor.TryParseQuantityChange(text, max: 2, oneBasedIndex: out var i, newQty: out var q)
            .Should().BeTrue();
        i.Should().Be(idx);
        q.Should().Be(qty);
    }

    [Theory]
    [InlineData("3 x 1")]   // idx out of range
    [InlineData("1 x 100")] // qty above cap
    [InlineData("uno por tres")]
    [InlineData("3")]       // missing operator
    [InlineData("")]
    public void TryParseQuantityChange_rejects_invalid_input(string text)
    {
        WebhookProcessor.TryParseQuantityChange(text, max: 2, oneBasedIndex: out _, newQty: out _)
            .Should().BeFalse();
    }

    [Fact]
    public void TryParseQuantityChange_allows_zero_for_explicit_removal()
    {
        WebhookProcessor.TryParseQuantityChange("1 x 0", max: 2, oneBasedIndex: out var i, newQty: out var q)
            .Should().BeTrue();
        i.Should().Be(1);
        q.Should().Be(0);
    }

    [Theory]
    [InlineData("btn_edit_remove", "quitar")]
    [InlineData("btn_edit_qty",    "cambiar cantidades")]
    [InlineData("btn_edit_redo",   "rehacer")]
    public void MapButtonIdToText_routes_edit_mode_buttons(string buttonId, string expectedText)
    {
        WebhookProcessor.MapButtonIdToText(buttonId).Should().Be(expectedText);
    }

    [Fact]
    public void EditModeMenuButtons_uses_the_three_new_ids()
    {
        var ids = WebhookProcessor.EditModeMenuButtons.Select(b => b.Id).ToList();
        ids.Should().Equal("btn_edit_remove", "btn_edit_qty", "btn_edit_redo");
    }
}
