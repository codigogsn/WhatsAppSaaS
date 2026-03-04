using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Infrastructure.Repositories;

public sealed class OrderRepository : IOrderRepository
{
    private readonly AppDbContext _db;

    public OrderRepository(AppDbContext db)
    {
        _db = db;
    }

    public async Task AddOrderAsync(Order order, CancellationToken ct = default)
    {
        // 1) Normalizar teléfono E.164 (clave lógica del Customer)
        var phoneE164 = NormalizeToE164(order.CustomerPhone) ?? NormalizeToE164(order.From) ?? "+0000000000";

        // 2) Upsert Customer (multi-tenant ready: BusinessId = null por ahora)
        var now = DateTime.UtcNow;

        var customer = await _db.Customers
            .FirstOrDefaultAsync(c => c.BusinessId == null && c.PhoneE164 == phoneE164, ct);

        if (customer is null)
        {
            customer = new Customer
            {
                BusinessId = null,
                PhoneE164 = phoneE164,
                Name = string.IsNullOrWhiteSpace(order.CustomerName) ? null : order.CustomerName.Trim(),
                TotalSpent = 0m,
                OrdersCount = 0,
                FirstSeenAtUtc = now,
                LastSeenAtUtc = now,
                LastPurchaseAtUtc = null
            };

            _db.Customers.Add(customer);
        }
        else
        {
            // Si antes no teníamos nombre y ahora sí, lo seteamos
            if (string.IsNullOrWhiteSpace(customer.Name) && !string.IsNullOrWhiteSpace(order.CustomerName))
                customer.Name = order.CustomerName.Trim();

            customer.LastSeenAtUtc = now;
        }

        // 3) Link Order -> Customer
        order.CustomerId = customer.Id;

        // 4) Montos: recalcular total si aplica (no rompe si UnitPrice está en 0)
        //    (Si no existe RecalculateTotal en tu Order, bórralo aquí)
        order.RecalculateTotal();

        // 5) Analytics denormalizados del customer (solo si la orden quedó completada)
        if (order.CheckoutCompleted)
        {
            customer.OrdersCount += 1;
            customer.LastPurchaseAtUtc = now;
            customer.TotalSpent += order.Total;
        }

        // 6) Guardar Order + Items
        _db.Orders.Add(order);
        await _db.SaveChangesAsync(ct);
    }

    // =========================
    // Helpers
    // =========================
    private static string? NormalizeToE164(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        // WhatsApp "from" suele venir como digits sin '+', ej: "584141627985"
        // CustomerPhone en planilla puede venir "0414..." o "+58..."
        var trimmed = input.Trim();

        // si ya viene con +
        if (trimmed.StartsWith("+"))
        {
            var digits = new string(trimmed.Where(char.IsDigit).ToArray());
            return digits.Length >= 8 ? $"+{digits}" : null;
        }

        // solo dígitos
        var onlyDigits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (onlyDigits.Length < 8) return null;

        // Caso VE: "0414..." -> "+58" + "414..."
        if (onlyDigits.StartsWith("0") && onlyDigits.Length >= 10)
            return $"+58{onlyDigits[1..]}";

        // Caso WA: "58414..." -> "+58414..."
        if (onlyDigits.StartsWith("58"))
            return $"+{onlyDigits}";

        // fallback: asumir que ya es número internacional sin +
        return $"+{onlyDigits}";
    }
}
