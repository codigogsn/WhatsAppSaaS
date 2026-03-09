using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Application.Services;

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

    public bool HandlesOwnFlow => false;

    public (string Id, string Label)[] FulfillmentOptions =>
    [
        ("btn_delivery", "Delivery"),
        ("btn_pickup", "Pickup")
    ];

    public string CheckoutFormMessage => Msg.CheckoutForm;

    public string GetGreeting(string businessName)
        => Msg.DefaultGreeting(businessName);
}
