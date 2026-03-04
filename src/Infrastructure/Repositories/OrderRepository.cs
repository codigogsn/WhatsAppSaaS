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
        // Normalizar teléfono
        var phoneE164 = NormalizeToE164(order.CustomerPhone) 
                        ?? NormalizeToE164(order.From) 
                        ?? "+0000000000";

        var now = DateTime.UtcNow;

        // Buscar customer existente
        var customer = await _db.Customers
            .FirstOrDefaultAsync(c => c.BusinessId == null && c.PhoneE164 == phoneE164, ct);

        if (customer == null)
        {
            customer = new Customer
            {
                BusinessId = null,
                PhoneE164 = phoneE164,
                Name = string.IsNullOrWhiteSpace(order.CustomerName) ? null : order.CustomerName.Trim(),
                TotalSpent = 0m,
                OrdersCount = 0,
                FirstSeenAtUtc = now,
                LastSeenAtUtc = now
            };

            _db.Customers.Add(customer);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(customer.Name) && !string.IsNullOrWhiteSpace(order.CustomerName))
                customer.Name = order.CustomerName.Trim();

            customer.LastSeenAtUtc = now;
        }

        // Link order → customer
        order.CustomerId = customer.Id;

        // Recalcular montos
        order.RecalculateTotal();

        // Actualizar analytics customer
        if (order.CheckoutCompleted)
        {
            customer.OrdersCount += 1;
            customer.LastPurchaseAtUtc = now;

            // 🔥 aquí estaba el bug (antes usaba order.Total)
            customer.TotalSpent += order.TotalAmount ?? 0m;
        }

        _db.Orders.Add(order);

        await _db.SaveChangesAsync(ct);
    }

    // =========================
    // Helpers
    // =========================

    private static string? NormalizeToE164(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var trimmed = input.Trim();

        if (trimmed.StartsWith("+"))
        {
            var digits = new string(trimmed.Where(char.IsDigit).ToArray());
            return digits.Length >= 8 ? $"+{digits}" : null;
        }

        var onlyDigits = new string(trimmed.Where(char.IsDigit).ToArray());

        if (onlyDigits.Length < 8)
            return null;

        // Venezuela: 0414xxxxxxx -> +58414xxxxxxx
        if (onlyDigits.StartsWith("0") && onlyDigits.Length >= 10)
            return $"+58{onlyDigits[1..]}";

        if (onlyDigits.StartsWith("58"))
            return $"+{onlyDigits}";

        return $"+{onlyDigits}";
    }
}
