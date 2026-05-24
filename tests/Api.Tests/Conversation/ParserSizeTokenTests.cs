using FluentAssertions;
using WhatsAppSaaS.Application.Services;

namespace WhatsAppSaaS.Api.Tests.Conversation;

/// <summary>
/// Regression tests for the explicit size-token disambiguation introduced for
/// the La Mina del Shawarma (La Boyera) production bug: customer wrote
/// "2 shawarmas pollo 350 gramos" but the parser resolved to the 200 g item.
/// Verifies that when the input contains a weight token, the parser prefers
/// the catalog item whose canonical encodes the same size — and otherwise
/// preserves the prior first-match behavior.
/// </summary>
public class ParserSizeTokenTests
{
    private static readonly WebhookProcessor.MenuEntry[] ShawarmaCatalog =
    [
        // Order matters: 200 grs comes first in the array, so without the size-token
        // fix the parser would always resolve "shawarma pollo" to the 200 grs row.
        new()
        {
            Canonical = "Shawarma Pollo 200 grs",
            Aliases = new[] { "shawarma", "shawarma pollo", "shawarmas pollo" },
            Category = "Shawarmas",
            Price = 6.00m
        },
        new()
        {
            Canonical = "Shawarma Pollo 350 grs",
            Aliases = new[] { "shawarma", "shawarma pollo", "shawarmas pollo" },
            Category = "Shawarmas",
            Price = 9.00m
        },
        new()
        {
            Canonical = "Shawarma Carne 200 grs",
            Aliases = new[] { "shawarma carne", "shawarmas carne" },
            Category = "Shawarmas",
            Price = 7.00m
        },
        new()
        {
            Canonical = "Shawarma Carne 350 grs",
            Aliases = new[] { "shawarma carne", "shawarmas carne" },
            Category = "Shawarmas",
            Price = 10.00m
        },
    ];

    public ParserSizeTokenTests()
    {
        WebhookProcessor.ActiveCatalog.Value = ShawarmaCatalog;
    }

    [Fact]
    public void ShawarmaPollo_350Gramos_ResolvesTo350grs()
    {
        var parsed = WebhookProcessor.ParseOrderText("2 shawarmas pollo 350 gramos");

        parsed.Should().HaveCount(1);
        parsed[0].Name.Should().Be("Shawarma Pollo 350 grs");
        parsed[0].Quantity.Should().Be(2);
    }

    [Fact]
    public void ShawarmaPollo_350Gr_ResolvesTo350grs()
    {
        var parsed = WebhookProcessor.ParseOrderText("2 shawarma pollo 350 gr");

        parsed.Should().HaveCount(1);
        parsed[0].Name.Should().Be("Shawarma Pollo 350 grs");
        parsed[0].Quantity.Should().Be(2);
    }

    [Fact]
    public void ShawarmaCarne_200Gramos_ResolvesTo200grs()
    {
        var parsed = WebhookProcessor.ParseOrderText("2 shawarma carne 200 gramos");

        parsed.Should().HaveCount(1);
        parsed[0].Name.Should().Be("Shawarma Carne 200 grs");
        parsed[0].Quantity.Should().Be(2);
    }

    [Fact]
    public void ShawarmaPollo_NoSize_PreservesFirstMatchBehavior()
    {
        // Without an explicit size token, the parser preserves its prior behavior
        // and returns the first catalog row that matches the base name — here 200 grs.
        var parsed = WebhookProcessor.ParseOrderText("2 shawarma pollo");

        parsed.Should().HaveCount(1);
        parsed[0].Name.Should().Be("Shawarma Pollo 200 grs");
        parsed[0].Quantity.Should().Be(2);
    }
}
