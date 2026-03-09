using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Application.Strategies;

public sealed class VerticalStrategyFactory : IVerticalStrategyFactory
{
    private static readonly RestaurantStrategy Restaurant = new();

    public IVerticalStrategy GetStrategy(string? verticalType)
    {
        return verticalType switch
        {
            "restaurant" => Restaurant,
            _ => Restaurant // safe default — all existing businesses are restaurants
        };
    }
}
