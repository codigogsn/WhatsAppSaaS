using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Application.DTOs;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Infrastructure.Services;

public sealed class AdminAnalyticsService : IAdminAnalyticsService
{
    private readonly AppDbContext _db;

    public AdminAnalyticsService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<AnalyticsSummaryDto> GetSummaryAsync(CancellationToken ct)
    {
        // Orders
        var totalOrders = await _db.Orders.AsNoTracking().CountAsync(ct);
        var completedOrders = await _db.Orders.AsNoTracking()
            .CountAsync(o => o.CheckoutCompleted, ct);

        var pendingOrders = totalOrders - completedOrders;

        // Revenue: usamos Orders.TotalAmount (nullable) y coalesce a 0
        var totalRevenue = await _db.Orders.AsNoTracking()
            .SumAsync(o => (decimal?)(o.TotalAmount ?? 0m), ct) ?? 0m;

        // Customers
        var totalCustomers = await _db.Customers.AsNoTracking().CountAsync(ct);

        return new AnalyticsSummaryDto
        {
            TotalOrders = totalOrders,
            CompletedOrders = completedOrders,
            PendingOrders = pendingOrders,
            TotalRevenue = totalRevenue,
            TotalCustomers = totalCustomers
        };
    }

    public async Task<List<TopProductDto>> GetTopProductsAsync(int take, CancellationToken ct)
    {
        if (take <= 0) take = 10;

        // IMPORTANTE:
        // - agregamos por OrderItems.Name (lo que realmente existe hoy)
        // - UnitPrice es nullable -> (UnitPrice ?? 0)
        // - Revenue = Sum((UnitPrice ?? 0) * Quantity)
        // - Todo 100% traducible a SQL en Npgsql
        var query =
            _db.OrderItems.AsNoTracking()
                .Where(i => i.Name != null && i.Name != "")
                .GroupBy(i => i.Name)
                .Select(g => new TopProductDto
                {
                    Name = g.Key,
                    TotalQuantity = g.Sum(x => x.Quantity),
                    TotalRevenue = g.Sum(x => (x.UnitPrice ?? 0m) * x.Quantity)
                })
                .OrderByDescending(x => x.TotalRevenue)
                .ThenByDescending(x => x.TotalQuantity)
                .Take(take);

        return await query.ToListAsync(ct);
    }

    public async Task<List<CustomerAnalyticsDto>> GetCustomersAsync(int take, CancellationToken ct)
    {
        if (take <= 0) take = 50;

        return await _db.Customers.AsNoTracking()
            .OrderByDescending(c => c.TotalSpent)
            .ThenByDescending(c => c.OrdersCount)
            .Take(take)
            .Select(c => new CustomerAnalyticsDto
            {
                PhoneE164 = c.PhoneE164,
                Name = c.Name,
                TotalSpent = c.TotalSpent,
                OrdersCount = c.OrdersCount,
                FirstSeenAtUtc = c.FirstSeenAtUtc,
                LastSeenAtUtc = c.LastSeenAtUtc,
                LastPurchaseAtUtc = c.LastPurchaseAtUtc
            })
            .ToListAsync(ct);
    }
}
