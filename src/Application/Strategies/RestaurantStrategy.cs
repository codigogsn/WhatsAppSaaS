using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Application.Strategies;

/// <summary>
/// Vertical strategy for restaurant businesses.
/// Wraps the existing hardcoded restaurant behavior — no logic changes.
/// </summary>
public sealed class RestaurantStrategy : IVerticalStrategy
{
    public string VerticalType => "restaurant";

    public bool RequiresGps(string? fulfillmentType)
        => fulfillmentType == "delivery";
}
