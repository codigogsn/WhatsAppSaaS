using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Application.Strategies;

public sealed class VerticalStrategyFactory : IVerticalStrategyFactory
{
    private static readonly RestaurantStrategy Restaurant = new();
    private static readonly FashionStrategy Fashion = new();

    public IVerticalStrategy GetStrategy(string? verticalType)
    {
        return verticalType switch
        {
            "restaurant" => Restaurant,
            "fashion_brand" => Fashion,
            _ => Restaurant // safe default
        };
    }
}
