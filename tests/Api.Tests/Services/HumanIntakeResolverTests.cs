using FluentAssertions;
using WhatsAppSaaS.Application.Services;
using Xunit;

namespace WhatsAppSaaS.Api.Tests.Services;

// Resolver-level tests for the Human OperatorDraft intake path. The matcher
// (MatchHumanIntakeMenuEntry) and ParseItemsFreeform must turn numeric-variant
// inputs like "combo #1" / "combo 01" / "combo uno" into the same menu entry
// without per-tenant alias hardcoding.
public sealed class HumanIntakeResolverTests
{
    // Mirrors the production La Mina menu where combos use the "Combo NN"
    // canonical (zero-padded) with aliases like "combo 1" / "combo uno".
    // This is the configuration that reproduces the live "combo #1 — revisar"
    // bug, and the canonical the resolver must echo back via OperatorDraftItem.
    private static readonly WebhookProcessor.MenuEntry[] TestMenu =
    {
        new() { Canonical = "Combo 01",            Aliases = ["combo 1", "combo uno"],     Price = 8.00m,  Category = "combos" },
        new() { Canonical = "Combo 02",            Aliases = ["combo 2", "combo dos"],     Price = 10.50m, Category = "combos" },
        new() { Canonical = "Combo 03",            Aliases = ["combo 3", "combo tres"],    Price = 11.00m, Category = "combos" },
        new() { Canonical = "Hamburguesa Clasica", Aliases = ["hamburguesa", "hamburguesas"], Price = 6.50m, Category = "hamburguesas" },
        new() { Canonical = "Coca Cola",           Aliases = ["coca cola", "coca cola 355"], Price = 1.50m, Category = "bebidas" },
        new() { Canonical = "Shawarma 350 grs",    Aliases = ["shawarma 350 gramos", "shawarma 350"], Price = 8.00m, Category = "shawarmas" },
    };

    private static (string Name, int Quantity, decimal? UnitPrice) ParseSingle(string itemText)
    {
        var pedido = "Pedido: " + itemText;
        var ok = WebhookProcessor.TryParseHumanIntake(pedido, TestMenu, out var intake);
        ok.Should().BeTrue($"input '{itemText}' should be parseable");
        intake.Items.Should().HaveCount(1, $"input '{itemText}' should yield exactly one item");
        var it = intake.Items[0];
        return (it.Name, it.Quantity, it.UnitPrice);
    }

    [Theory]
    [InlineData("combo #1")]
    [InlineData("combo 1")]
    [InlineData("combo uno")]
    [InlineData("combo 01")]
    [InlineData("1 combo #1")]
    public void ComboNumericVariants_ResolveToCombo01(string input)
    {
        var (name, qty, price) = ParseSingle(input);
        name.Should().Be("Combo 01");
        qty.Should().Be(1);
        price.Should().Be(8.00m);
    }

    [Fact]
    public void Regression_Shawarma350Pollo_StillResolves()
    {
        var (name, qty, price) = ParseSingle("shawarma 350 pollo");
        name.Should().Be("Shawarma 350 grs");
        qty.Should().Be(1);
        price.Should().Be(8.00m);
    }

    [Fact]
    public void Regression_TwoHamburguesas_StillResolves()
    {
        var (name, qty, price) = ParseSingle("2 hamburguesas");
        name.Should().Be("Hamburguesa Clasica");
        qty.Should().Be(2);
        price.Should().Be(6.50m);
    }

    [Fact]
    public void Regression_OneCocaCola355_StillResolves()
    {
        var (name, qty, price) = ParseSingle("1 coca cola 355");
        name.Should().Be("Coca Cola");
        qty.Should().Be(1);
        price.Should().Be(1.50m);
    }
}
