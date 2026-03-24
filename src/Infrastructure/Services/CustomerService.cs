using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Application.Services;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Infrastructure.Services;

public sealed class CustomerService
{
    private readonly AppDbContext _db;

    public CustomerService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Upsert de Customer por (BusinessId, PhoneE164). Por ahora BusinessId queda null.
    /// Actualiza contadores y timestamps de analytics.
    /// </summary>
    public async Task<Customer> UpsertFromOrderAsync(Order order, CancellationToken ct)
    {
        // Clave lógica (E.164): hoy usamos Order.From tal cual llega de WhatsApp.
        // Si luego quieres normalizar, lo hacemos sin romper (pero hoy no nos desviamos).
        var phone = order.From;
        var normalizedName = !string.IsNullOrWhiteSpace(order.CustomerName)
            ? WebhookProcessor.NormalizeDisplayName(order.CustomerName)
            : order.CustomerName;

        var customer = await _db.Customers
            .FirstOrDefaultAsync(c => c.BusinessId == null && c.PhoneE164 == phone, ct);

        var now = DateTime.UtcNow;

        if (customer == null)
        {
            customer = new Customer
            {
                BusinessId = null,
                PhoneE164 = phone,
                Name = normalizedName,
                TotalSpent = 0m,
                OrdersCount = 0,
                FirstSeenAtUtc = now,
                LastSeenAtUtc = now,
                LastPurchaseAtUtc = null
            };

            _db.Customers.Add(customer);
            await _db.SaveChangesAsync(ct);
        }
        else
        {
            customer.LastSeenAtUtc = now;

            // Mejora: si antes estaba null y ya llegó, lo guardamos
            if (string.IsNullOrWhiteSpace(customer.Name) && !string.IsNullOrWhiteSpace(normalizedName))
                customer.Name = normalizedName;
            // Re-normalize existing name if it has formatting noise
            else if (!string.IsNullOrWhiteSpace(customer.Name))
                customer.Name = WebhookProcessor.NormalizeDisplayName(customer.Name);

            await _db.SaveChangesAsync(ct);
        }

        return customer;
    }

    /// <summary>
    /// Llamar una sola vez cuando una orden queda confirmada/finalizada para sumar analytics.
    /// </summary>
    public async Task ApplyPurchaseAsync(Customer customer, Order order, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // Si tu Order no tiene Total/Subtotal, no inventamos.
        // Por ahora sumamos 0 y dejamos listo el hook para cuando montos estén 100% conectados.
        // Si ya tienes TotalAmount/SubtotalAmount en Order, cámbialo aquí por order.TotalAmount ?? 0m.
        var amount = 0m;

        customer.TotalSpent += amount;
        customer.OrdersCount += 1;
        customer.LastPurchaseAtUtc = now;
        customer.LastSeenAtUtc = now;

        await _db.SaveChangesAsync(ct);
    }
}
