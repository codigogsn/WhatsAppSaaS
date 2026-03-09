using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Application.Strategies;

/// <summary>
/// Vertical strategy for fashion brand businesses.
/// Handles structured product orders from web catalog → WhatsApp checkout.
/// </summary>
public sealed class FashionStrategy : IVerticalStrategy
{
    public string VerticalType => "fashion_brand";

    public bool RequiresGps(string? fulfillmentType) => false;

    public bool HandlesOwnFlow => true;

    public (string Id, string Label)[] FulfillmentOptions =>
    [
        ("btn_shipping", "Envío"),
        ("btn_store_pickup", "Retiro en tienda")
    ];

    public string CheckoutFormMessage =>
        "\ud83d\udcdd *DATOS PARA TU PEDIDO*\n\n"
        + "\ud83d\udc64 *Nombre:*\n"
        + "\ud83d\udcf1 *Teléfono:*\n"
        + "\ud83c\udfe1 *Dirección de envío:*\n\n"
        + "Responde con tus datos y luego escribe *CONFIRMAR*.";

    public string GetGreeting(string businessName)
        => $"Hola, bienvenido a *{businessName}* \ud83d\udc4b\n\n"
         + "Envía los datos de tu pedido o visita nuestro catálogo para elegir.";
}
