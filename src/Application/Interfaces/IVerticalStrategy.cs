namespace WhatsAppSaaS.Application.Interfaces;

/// <summary>
/// Defines vertical-specific behavior for the ordering flow.
/// Each business vertical (restaurant, fashion, services, etc.)
/// implements this to customize checkout, fulfillment, and validation.
/// </summary>
public interface IVerticalStrategy
{
    string VerticalType { get; }

    /// <summary>
    /// Whether GPS location is required for the given fulfillment type.
    /// </summary>
    bool RequiresGps(string? fulfillmentType);

    /// <summary>
    /// Whether this vertical handles its own message processing loop
    /// (bypasses the default restaurant flow).
    /// </summary>
    bool HandlesOwnFlow { get; }

    /// <summary>
    /// Fulfillment option labels for buttons (e.g. "Delivery"/"Pickup" or "Envío"/"Retiro en tienda").
    /// </summary>
    (string Id, string Label)[] FulfillmentOptions { get; }

    /// <summary>
    /// Checkout form message shown when collecting customer data.
    /// </summary>
    string CheckoutFormMessage { get; }

    /// <summary>
    /// Greeting message for new conversations.
    /// </summary>
    string GetGreeting(string businessName);
}

/// <summary>
/// Resolves the correct IVerticalStrategy for a given business vertical type.
/// </summary>
public interface IVerticalStrategyFactory
{
    IVerticalStrategy GetStrategy(string? verticalType);
}
