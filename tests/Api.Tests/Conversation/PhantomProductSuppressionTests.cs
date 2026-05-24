using FluentAssertions;
using WhatsAppSaaS.Application.Services;

namespace WhatsAppSaaS.Api.Tests.Conversation;

/// <summary>
/// Phase-1 regression tests for the demo-catalog-leak fix.
///
/// Before the fix, NormalizeMenuItemName(rawItem) retried the hardcoded
/// MenuCatalog when the active tenant catalog couldn't resolve the customer
/// text. Real La Boyera orders had phantom products like "Coca Cola",
/// "Combo Clasico", "Papas Medianas", and "Hamburguesa Clasica" written
/// to them. After the fix, the demo retry is gated on ActiveCatalogIsDemo.
/// Empty/new tenants and dev/preview still see the demo fallback; real
/// tenants (catalog loaded from DB) see "no match" instead of phantoms.
/// </summary>
public class PhantomProductSuppressionTests
{
    // Real-tenant catalog (La Boyera-shaped): only shawarmas + local refrescos.
    // Contains no "Coca Cola" and no "Hamburguesa Clasica" — exactly the menu
    // shape that produced phantom products in production.
    private static readonly WebhookProcessor.MenuEntry[] LaBoyeraCatalog =
    [
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
            Canonical = "Refresco de lata",
            Aliases = System.Array.Empty<string>(),
            Category = "Bebidas",
            Price = 2.00m
        },
    ];

    [Fact]
    public void EmptyTenant_Cocacolas_StillResolvesViaDemoFallback()
    {
        // Empty/new tenant: ActiveCatalog points at the demo MenuCatalog and
        // IsDemo is true. "2 cocacolas" must still resolve so onboarding and
        // the dev/preview Demo Restaurant tenant keep working.
        WebhookProcessor.ActiveCatalog.Value = WebhookProcessor.MenuCatalog;
        WebhookProcessor.ActiveCatalogIsDemo.Value = true;

        var parsed = WebhookProcessor.ParseOrderText("2 cocacolas");

        parsed.Should().HaveCount(1);
        parsed[0].Name.Should().Be("Coca Cola");
        parsed[0].Quantity.Should().Be(2);
    }

    [Fact]
    public void LaBoyera_Cocacolas_DropsItem_NoPhantomCocaCola()
    {
        // Real tenant: ActiveCatalog has real DB items and IsDemo is false.
        // "Coca Cola" does NOT exist in this menu — the parser must NOT leak
        // the demo "Coca Cola" canonical into the cart. The segment is
        // silently dropped (Phase 2 will add a customer clarification prompt;
        // this test locks in the Phase 1 "no phantom" guarantee).
        WebhookProcessor.ActiveCatalog.Value = LaBoyeraCatalog;
        WebhookProcessor.ActiveCatalogIsDemo.Value = false;

        var parsed = WebhookProcessor.ParseOrderText("2 cocacolas");

        parsed.Should().NotContain(p => p.Name.Equals("Coca Cola", System.StringComparison.OrdinalIgnoreCase),
            "real tenants must never have demo 'Coca Cola' written into the cart");
    }

    [Fact]
    public void LaBoyera_HamburguesaClasica_DropsItem_NoPhantomHamburger()
    {
        // Same guarantee for "Hamburguesa Clasica" — appeared in a real
        // La Boyera cancelled order (af781131-…) before this fix.
        WebhookProcessor.ActiveCatalog.Value = LaBoyeraCatalog;
        WebhookProcessor.ActiveCatalogIsDemo.Value = false;

        var parsed = WebhookProcessor.ParseOrderText("1 hamburguesa clasica");

        parsed.Should().NotContain(p => p.Name.Equals("Hamburguesa Clasica", System.StringComparison.OrdinalIgnoreCase),
            "real tenants must never have demo 'Hamburguesa Clasica' written into the cart");
    }

    [Fact]
    public void LaBoyera_ShawarmaPollo350Gramos_StillResolvesTo350grs()
    {
        // Regression check: the prior size-token fix must continue to work
        // under the new IsDemo gate. Active catalog has both 200 and 350
        // shawarma rows; the customer-typed "350 gramos" must still promote
        // the match to the 350 grs row.
        WebhookProcessor.ActiveCatalog.Value = LaBoyeraCatalog;
        WebhookProcessor.ActiveCatalogIsDemo.Value = false;

        var parsed = WebhookProcessor.ParseOrderText("2 shawarmas pollo 350 gramos");

        parsed.Should().HaveCount(1);
        parsed[0].Name.Should().Be("Shawarma Pollo 350 grs");
        parsed[0].Quantity.Should().Be(2);
    }
}
