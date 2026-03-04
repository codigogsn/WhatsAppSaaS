using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        var totalOrders = await _db.Orders.CountAsync(ct);

        var completedOrders = await _db.Orders
            .Where(o => o.CheckoutCompleted)
            .CountAsync(ct);

        var pendingOrders = totalOrders - completedOrders;

        var totalRevenue = await _db.Orders
            .Where(o => o.CheckoutCompleted)
            .SumAsync(o => (decimal?)(o.TotalAmount ?? 0m), ct) ?? 0m;

        var totalCustomers = await _db.Customers.CountAsync(ct);

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
        take = Math.Clamp(take, 1, 100);

        var data = await _db.OrderItems
            .GroupBy(i => i.Name)
            .Select(g => new TopProductDto
            {
                Name = g.Key,
                TotalQuantity = g.Sum(x => x.Quantity),
                TotalRevenue = g.Sum(x => (x.UnitPrice ?? 0m) * x.Quantity)
            })
            .OrderByDescending(x => x.TotalQuantity)
            .ThenByDescending(x => x.TotalRevenue)
            .Take(take)
            .ToListAsync(ct);

        return data;
    }

    public async Task<List<CustomerAnalyticsDto>> GetCustomersAsync(int take = 50, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 200);

        var data = await _db.Customers
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

        return data;
    }
}
