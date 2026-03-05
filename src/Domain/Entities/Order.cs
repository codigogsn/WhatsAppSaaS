using System;
using System.Collections.Generic;
using System.Linq;

namespace WhatsAppSaaS.Domain.Entities;

public class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? BusinessId { get; set; }

    public string From { get; set; } = default!;
    public string PhoneNumberId { get; set; } = default!;

    public string DeliveryType { get; set; } = default!;

    public string Status { get; set; } = "Pending";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    // ==============================
    // CHECKOUT DETAILS
    // ==============================
    public string? CustomerName { get; set; }
    public string? CustomerIdNumber { get; set; }
    public string? CustomerPhone { get; set; }
    public string? Address { get; set; }
    public string? PaymentMethod { get; set; }
    public string? ReceiverName { get; set; }
    public string? AdditionalNotes { get; set; }

    public decimal? LocationLat { get; set; }
    public decimal? LocationLng { get; set; }
    public string? LocationText { get; set; }

    public bool CheckoutFormSent { get; set; }
    public bool CheckoutCompleted { get; set; }
    public DateTime? CheckoutCompletedAtUtc { get; set; }

    // 🛡️ Guards anti doble notificación (Paso 1)
    public string? LastNotifiedStatus { get; set; }
    public DateTime? LastNotifiedAtUtc { get; set; }

    // 👤 Link a Customer (nullable FK)
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    // 🧾 Montos
    public decimal? SubtotalAmount { get; set; }
    public decimal? DeliveryFee { get; set; }
    public decimal? TotalAmount { get; set; }

    // ⏱️ Operational timestamps
    public DateTime? AcceptedAtUtc { get; set; }
    public DateTime? PreparingAtUtc { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }

    // Lista de items
    public List<OrderItem> Items { get; set; } = new();

    public void RecalculateTotal()
    {
        var subtotal = Items.Sum(i => (i.UnitPrice ?? 0m) * i.Quantity);
        SubtotalAmount = subtotal;
        TotalAmount = subtotal + (DeliveryFee ?? 0m);
    }
}
