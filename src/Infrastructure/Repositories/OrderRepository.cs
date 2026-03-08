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

        // Buscar customer existente (scoped to business)
        var customer = await _db.Customers
            .FirstOrDefaultAsync(c => c.BusinessId == order.BusinessId && c.PhoneE164 == phoneE164, ct);

        if (customer == null)
        {
            customer = new Customer
            {
                BusinessId = order.BusinessId,
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
            customer.TotalSpent += order.TotalAmount ?? 0m;

            // Save last delivery address for quick reorder
            if (!string.IsNullOrWhiteSpace(order.Address))
                customer.LastDeliveryAddress = order.Address;
        }

        _db.Orders.Add(order);

        await _db.SaveChangesAsync(ct);
    }

    public async Task<Order?> GetLastCompletedOrderAsync(string fromPhone, Guid businessId, CancellationToken ct = default)
    {
        return await _db.Orders
            .Include(o => o.Items)
            .Where(o => o.BusinessId == businessId
                     && o.From == fromPhone
                     && o.CheckoutCompleted)
            .OrderByDescending(o => o.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Customer?> GetCustomerByPhoneAsync(string fromPhone, Guid businessId, CancellationToken ct = default)
    {
        var phoneE164 = NormalizeToE164(fromPhone) ?? fromPhone;
        return await _db.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.BusinessId == businessId && c.PhoneE164 == phoneE164, ct);
    }

    public async Task<bool> AttachPaymentProofAsync(Guid orderId, string mediaId, CancellationToken ct = default)
    {
        var order = await _db.Orders.FindAsync(new object[] { orderId }, ct);
        if (order is null) return false;

        order.PaymentProofMediaId = mediaId;
        order.PaymentProofSubmittedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
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
