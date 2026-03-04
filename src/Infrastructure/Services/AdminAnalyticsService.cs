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
        var ordersQuery = _db.Orders.AsNoTracking();

        var ordersCount = await ordersQuery.CountAsync(ct);
        var completedCount = await ordersQuery.Where(o => o.CheckoutCompleted).CountAsync(ct);

        var customersCount = await _db.Customers.AsNoTracking().CountAsync(ct);

        var totalRevenue = await ordersQuery
            .Where(o => o.TotalAmount != null)
            .SumAsync(o => o.TotalAmount ?? 0m, ct);

        var lastOrderAt = await ordersQuery
            .OrderByDescending(o => o.CreatedAtUtc)
            .Select(o => (DateTime?)o.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

        var avg = ordersCount > 0 ? (totalRevenue / ordersCount) : 0m;

        return new AnalyticsSummaryDto
        {
            OrdersCount = ordersCount,
            OrdersCompletedCount = completedCount,
            CustomersCount = customersCount,
            TotalRevenue = totalRevenue,
            AvgOrderValue = avg,
            LastOrderAtUtc = lastOrderAt
        };
    }

    public async Task<List<TopProductDto>> GetTopProductsAsync(int take = 10, CancellationToken ct = default)
    {
        take = take <= 0 ? 10 : take;
        take = take > 50 ? 50 : take;

        // Top por quantity sold; revenue por LineTotal (si existe) sino UnitPrice*Quantity
        var items = await _db.OrderItems
            .AsNoTracking()
            .Select(i => new
            {
                i.Name,
                i.Quantity,
                Line = (i.LineTotal ?? ((i.UnitPrice ?? 0m) * i.Quantity))
            })
            .ToListAsync(ct);

        return items
            .GroupBy(x => x.Name.Trim().ToLowerInvariant())
            .Select(g => new TopProductDto
            {
                Name = g.First().Name,
                QuantitySold = g.Sum(x => x.Quantity),
                Revenue = g.Sum(x => x.Line)
            })
            .OrderByDescending(x => x.QuantitySold)
            .ThenByDescending(x => x.Revenue)
            .Take(take)
            .ToList();
    }

    public async Task<List<CustomerAnalyticsDto>> GetCustomersAsync(int take = 50, CancellationToken ct = default)
    {
        take = take <= 0 ? 50 : take;
        take = take > 200 ? 200 : take;

        return await _db.Customers
            .AsNoTracking()
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
                LastPurchaseAtUtc = c.LastPurchaseAtUtc
            })
            .ToListAsync(ct);
    }
}
