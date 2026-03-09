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
}

/// <summary>
/// Resolves the correct IVerticalStrategy for a given business vertical type.
/// </summary>
public interface IVerticalStrategyFactory
{
    IVerticalStrategy GetStrategy(string? verticalType);
}
