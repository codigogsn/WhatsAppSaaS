namespace WhatsAppSaaS.Domain.Entities;

public class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string From { get; set; } = default!;           // Teléfono del cliente
    public string PhoneNumberId { get; set; } = default!;  // WhatsApp phoneNumberId

    public string DeliveryType { get; set; } = default!;   // "pickup" | "delivery"

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<OrderItem> Items { get; set; } = new();
}
