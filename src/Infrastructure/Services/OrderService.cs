using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Domain.Exceptions;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Infrastructure.Services;

public sealed class OrderService
{
    private readonly AppDbContext _db;
    private readonly ILogger<OrderService> _logger;

    public OrderService(AppDbContext db, ILogger<OrderService> logger)
    {
        _db = db;
        _logger = logger;
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

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex, "CONCURRENCY CONFLICT on OrderId={OrderId}", order.Id);
            throw new ConcurrencyException("Order was modified by another process.");
        }
    }

    public async Task MarkCheckoutCompletedAsync(Order order, CancellationToken ct)
    {
        order.CheckoutCompleted = true;
        order.CheckoutCompletedAtUtc = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _logger.LogWarning(ex, "CONCURRENCY CONFLICT on OrderId={OrderId}", order.Id);
            throw new ConcurrencyException("Order was modified by another process.");
        }
    }
}
