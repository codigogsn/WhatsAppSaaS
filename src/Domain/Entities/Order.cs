namespace WhatsAppSaaS.Domain.Entities;

public class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string From { get; set; } = default!;           // Teléfono del cliente
    public string PhoneNumberId { get; set; } = default!;  // WhatsApp phoneNumberId

    public string DeliveryType { get; set; } = default!;   // "pickup" | "delivery"

    // ✅ Fase 4A: estado del pedido (Pending, Accepted, InProgress, OnTheWay, Delivered, Cancelled, etc.)
    public string Status { get; set; } = "Pending";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    // ==============================
    // PLANILLA / CHECKOUT DETAILS
    // ==============================
    public string? CustomerName { get; set; }
    public string? CustomerIdNumber { get; set; }
    public string? CustomerPhone { get; set; }
    public string? Address { get; set; }
    public string? PaymentMethod { get; set; }
    public string? ReceiverName { get; set; }
    public string? AdditionalNotes { get; set; }

    // GPS (si el usuario manda pin) / o texto
    public decimal? LocationLat { get; set; }
    public decimal? LocationLng { get; set; }
    public string? LocationText { get; set; }

    public bool CheckoutFormSent { get; set; }
    public bool CheckoutCompleted { get; set; }
    public DateTime? CheckoutCompletedAtUtc { get; set; }

    public List<OrderItem> Items { get; set; } = new();
}
