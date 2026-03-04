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
        var ordersCount = await _db.Orders.AsNoTracking().CountAsync(ct);
        var customersCount = await _db.Customers.AsNoTracking().CountAsync(ct);

        // 1) Preferir totals guardados (TotalAmount) si existen
        var ordersRevenue = await _db.Orders
            .AsNoTracking()
            .SumAsync(o => (decimal?)(o.TotalAmount ?? 0m), ct) ?? 0m;

        // 2) Fallback: calcular desde items si totals está en 0 (MVP para órdenes viejas)
        decimal itemsRevenue = 0m;
        if (ordersRevenue == 0m)
        {
            itemsRevenue = await _db.OrderItems
                .AsNoTracking()
                .SumAsync(i => (decimal?)((i.UnitPrice ?? 0m) * i.Quantity), ct) ?? 0m;
        }

        var totalRevenue = ordersRevenue > 0m ? ordersRevenue : itemsRevenue;
        var avgOrderValue = ordersCount > 0 ? totalRevenue / ordersCount : 0m;

        return new AnalyticsSummaryDto
        {
            OrdersCount = ordersCount,
            CustomersCount = customersCount,
            TotalRevenue = totalRevenue,
            AvgOrderValue = avgOrderValue
        };
    }

    public async Task<List<TopProductDto>> GetTopProductsAsync(int take = 10, CancellationToken ct = default)
    {
        if (take <= 0) take = 10;
        if (take > 50) take = 50;

        var list = await _db.OrderItems
            .AsNoTracking()
            .GroupBy(i => i.Name)
            .Select(g => new TopProductDto
            {
                Name = g.Key,
                Quantity = g.Sum(x => x.Quantity),
                Revenue = g.Sum(x => (x.UnitPrice ?? 0m) * x.Quantity)
            })
            .OrderByDescending(x => x.Quantity)
            .ThenByDescending(x => x.Revenue)
            .Take(take)
            .ToListAsync(ct);

        return list;
    }

    public async Task<List<CustomerAnalyticsDto>> GetCustomersAsync(int take = 50, CancellationToken ct = default)
    {
        if (take <= 0) take = 50;
        if (take > 200) take = 200;

        var list = await _db.Customers
            .AsNoTracking()
            .OrderByDescending(c => c.TotalSpent)
            .ThenByDescending(c => c.OrdersCount)
            .Take(take)
            .Select(c => new CustomerAnalyticsDto
            {
                Id = c.Id,
                PhoneE164 = c.PhoneE164,
                Name = c.Name,
                TotalSpent = c.TotalSpent,
                OrdersCount = c.OrdersCount,
                FirstSeenAtUtc = c.FirstSeenAtUtc,
                LastSeenAtUtc = c.LastSeenAtUtc,
                LastPurchaseAtUtc = c.LastPurchaseAtUtc
            })
            .ToListAsync(ct);

        return list;
    }
}
