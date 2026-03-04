using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Infrastructure.Services;

public sealed class OrderService
{
    private readonly AppDbContext _db;

    public OrderService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<Order> GetOrCreateOpenOrderAsync(
        string fromPhone,
        string phoneNumberId,
        CancellationToken ct)
    {
        var order = await _db.Orders
            .Include(x => x.Items)
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(x =>
                x.From == fromPhone &&
                x.PhoneNumberId == phoneNumberId &&
                x.Status == "Pending",
                ct);

        if (order != null)
            return order;

        order = new Order
        {
            From = fromPhone,
            PhoneNumberId = phoneNumberId,
            Status = "Pending",
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.Orders.Add(order);
        await _db.SaveChangesAsync(ct);

        return order;
    }

    public async Task AddItemAsync(
        Order order,
        string name,
        int quantity,
        decimal unitPrice,
        CancellationToken ct)
    {
        var item = new OrderItem
        {
            OrderId = order.Id,
            Name = name,
            Quantity = quantity,
            UnitPrice = unitPrice
        };

        order.Items.Add(item);

        // ✅ Nada de Total/Subtotal aquí porque tu entidad Order no los tiene
        await _db.SaveChangesAsync(ct);
    }

    public async Task MarkCheckoutCompletedAsync(Order order, CancellationToken ct)
    {
        order.CheckoutCompleted = true;
        order.CheckoutCompletedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
    }
}
