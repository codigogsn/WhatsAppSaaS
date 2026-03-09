using WhatsAppSaaS.Application.Services;

namespace WhatsAppSaaS.Api.Tests;

/// <summary>
/// Provides a shared test catalog that includes extras entries for modifier resolution tests.
/// The production MenuCatalog no longer includes extras, but the modifier engine
/// still supports them when a business catalog has extras items.
/// </summary>
internal static class TestCatalogHelper
{
    private static readonly WebhookProcessor.MenuEntry[] ExtraEntries =
    [
        new() { Canonical = "Extra Queso", Aliases = ["extra queso", "queso extra", "mas queso"],
            Category = "extras", Price = 1.00m },
        new() { Canonical = "Extra Tocineta", Aliases = ["extra tocineta", "tocineta extra",
            "extra bacon", "mas tocineta", "extra rocineta", "rocineta extra"],
            Category = "extras", Price = 1.50m },
        new() { Canonical = "Extra Carne", Aliases = ["extra carne", "carne extra", "doble carne", "mas carne"],
            Category = "extras", Price = 2.50m },
        new() { Canonical = "Extra Huevo", Aliases = ["extra huevo", "huevo extra", "con huevo", "mas huevo"],
            Category = "extras", Price = 1.00m },
    ];

    internal static WebhookProcessor.MenuEntry[] MenuCatalogWithExtras { get; } =
        WebhookProcessor.MenuCatalog.Concat(ExtraEntries).ToArray();
}
