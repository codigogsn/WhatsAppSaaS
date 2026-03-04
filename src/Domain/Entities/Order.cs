using System;
using System.Collections.Generic;
using System.Linq;

namespace WhatsAppSaaS.Domain.Entities;

public class Order
{
    public Guid Id { get; set; } = Guid.NewGuid();

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

    // 🧾 Montos (Paso 2)
    public decimal? SubtotalAmount { get; set; }
    public decimal? TotalAmount { get; set; }

    // 👤 Customers (Paso 3)
    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public List<OrderItem> Items { get; set; } = new();

    // 🧠 Helper: recalcular montos (null-safe)
    public void RecalculateAmounts()
    {
        var subtotal = Items.Sum(i => i.UnitPrice.GetValueOrDefault() * i.Quantity);
        SubtotalAmount = subtotal;
        TotalAmount = subtotal; // luego metemos fees/delivery si aplica
    }
}
