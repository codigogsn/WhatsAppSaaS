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

    public async Task<AnalyticsSummaryDto> GetSummaryAsync(CancellationToken ct = default)
    {
        var totalOrders = await _db.Orders.AsNoTracking().CountAsync(ct);

        // Definimos "completed" como CheckoutCompleted == true (MVP)
        var completedOrders = await _db.Orders.AsNoTracking()
            .CountAsync(o => o.CheckoutCompleted, ct);

        var pendingOrders = totalOrders - completedOrders;

        // TotalRevenue: usa TotalAmount si existe; si es null, cuenta 0
        var totalRevenue = await _db.Orders.AsNoTracking()
            .Where(o => o.CheckoutCompleted)
            .SumAsync(o => o.TotalAmount ?? 0m, ct);

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

    public async Task<List<TopProductDto>> GetTopProductsAsync(int take = 10, CancellationToken ct = default)
    {
        if (take <= 0) take = 10;
        if (take > 100) take = 100;

        // MVP: top-products sale de OrderItems (no dependemos de Products)
        // IMPORTANT: UnitPrice/LineTotal pueden ser null => coalesce a 0
        var rows = await _db.OrderItems
            .AsNoTracking()
            .Join(
                _db.Orders.AsNoTracking(),
                oi => oi.OrderId,
                o => o.Id,
                (oi, o) => new { oi, o }
            )
            .Where(x => x.o.CheckoutCompleted)
            .GroupBy(x => x.oi.Name)
            .Select(g => new
            {
                Name = g.Key,
                TotalQuantity = g.Sum(x => (int?)x.oi.Quantity) ?? 0,
                TotalRevenue = g.Sum(x =>
                    ((x.oi.UnitPrice ?? 0m) * x.oi.Quantity)
                )
            })
            .OrderByDescending(x => x.TotalQuantity)
            .ThenByDescending(x => x.TotalRevenue)
            .Take(take)
            .ToListAsync(ct);

        return rows
            .Select(x => new TopProductDto
            {
                Name = x.Name,
                TotalQuantity = x.TotalQuantity,
                TotalRevenue = x.TotalRevenue
            })
            .ToList();
    }

    public async Task<List<CustomerAnalyticsDto>> GetCustomersAsync(int take = 50, CancellationToken ct = default)
    {
        if (take <= 0) take = 50;
        if (take > 200) take = 200;

        var rows = await _db.Customers.AsNoTracking()
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

        return rows;
    }
}
