using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Infrastructure.Persistence;

public sealed class CustomerService
{
    private readonly AppDbContext _db;

    public CustomerService(AppDbContext db)
    {
        _db = db;
    }

    // Upsert por (BusinessId, PhoneE164). BusinessId null por ahora.
    public async Task<Customer> UpsertSeenAsync(
        Guid? businessId,
        string phoneE164,
        string? name,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        phoneE164 = phoneE164.Trim();

        var customer = await _db.Customers
            .FirstOrDefaultAsync(c => c.BusinessId == businessId && c.PhoneE164 == phoneE164, ct);

        if (customer is null)
        {
            customer = new Customer
            {
                BusinessId = businessId,
                PhoneE164 = phoneE164,
                Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                TotalSpent = 0m,
                OrdersCount = 0,
                FirstSeenAtUtc = nowUtc,
                LastSeenAtUtc = nowUtc,
                LastPurchaseAtUtc = null
            };

            _db.Customers.Add(customer);
            await _db.SaveChangesAsync(ct);
            return customer;
        }

        // Update liviano
        customer.LastSeenAtUtc = nowUtc;

        // Si trae name válido, lo guardamos (no pisamos con null/vacío)
        if (!string.IsNullOrWhiteSpace(name))
            customer.Name = name.Trim();

        await _db.SaveChangesAsync(ct);
        return customer;
    }

    public async Task ApplyPurchaseAsync(
        Customer customer,
        decimal amount,
        DateTime nowUtc,
        CancellationToken ct = default)
    {
        if (amount < 0) amount = 0;

        customer.OrdersCount += 1;
        customer.TotalSpent += amount;
        customer.LastPurchaseAtUtc = nowUtc;
        customer.LastSeenAtUtc = nowUtc;

        await _db.SaveChangesAsync(ct);
    }
}
