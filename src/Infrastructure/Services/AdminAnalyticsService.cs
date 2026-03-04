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

    public async Task<AnalyticsSummaryDto> GetSummaryAsync(CancellationToken ct)
    {
        // Conteos simples (server-side)
        var totalOrdersTask = _db.Orders.AsNoTracking().CountAsync(ct);
        var completedOrdersTask = _db.Orders.AsNoTracking().CountAsync(o => o.CheckoutCompleted, ct);
        var totalCustomersTask = _db.Customers.AsNoTracking().CountAsync(ct);

        await Task.WhenAll(totalOrdersTask, completedOrdersTask, totalCustomersTask);

        var totalOrders = totalOrdersTask.Result;
        var completedOrders = completedOrdersTask.Result;

        // Revenue MVP: sum de TotalAmount (nullable) solo en completadas
        // OJO: TotalAmount puede ser null => COALESCE a 0m
        var totalRevenue = await _db.Orders
            .AsNoTracking()
            .Where(o => o.CheckoutCompleted)
            .Select(o => o.TotalAmount ?? 0m)
            .SumAsync(ct);

        return new AnalyticsSummaryDto
        {
            TotalOrders = totalOrders,
            CompletedOrders = completedOrders,
            PendingOrders = Math.Max(0, totalOrders - completedOrders),
            TotalRevenue = totalRevenue,
            TotalCustomers = totalCustomersTask.Result
        };
    }

    public async Task<List<TopProductDto>> GetTopProductsAsync(int take, CancellationToken ct)
    {
        take = ClampTake(take);

        // ✅ Postgres-safe:
        // - Agrupamos por Name normalizado
        // - TotalQuantity = SUM(Quantity)
        // - TotalRevenue = SUM(Quantity * COALESCE(UnitPrice, 0))
        // - Todo se calcula server-side
        var query = _db.OrderItems
            .AsNoTracking()
            .Where(oi => oi.Quantity > 0 && oi.Name != null && oi.Name != "")
            .GroupBy(oi => oi.Name!.Trim().ToLower())
            .Select(g => new TopProductDto
            {
                Name = g.Key,
                TotalQuantity = g.Sum(x => x.Quantity),
                TotalRevenue = g.Sum(x => (x.UnitPrice ?? 0m) * (decimal)x.Quantity)
            })
            .OrderByDescending(x => x.TotalQuantity)
            .ThenByDescending(x => x.TotalRevenue)
            .Take(take);

        var result = await query.ToListAsync(ct);

        // Si quieres el nombre "bonito" (sin lower), puedes TitleCase luego,
        // pero NO lo hago aquí para no romper traducción EF.
        return result;
    }

    public async Task<List<CustomerAnalyticsDto>> GetCustomersAsync(int take, CancellationToken ct)
    {
        take = ClampTake(take);

        // Ya te está funcionando, lo dejo simple y seguro
        return await _db.Customers
            .AsNoTracking()
            .OrderByDescending(c => c.LastPurchaseAtUtc ?? c.LastSeenAtUtc ?? c.FirstSeenAtUtc)
            .Take(take)
            .Select(c => new CustomerAnalyticsDto
            {
                PhoneE164 = c.PhoneE164,
                Name = c.Name,
                TotalSpent = c.TotalSpent,
                OrdersCount = c.OrdersCount,
                FirstSeenAtUtc = c.FirstSeenAtUtc,
                LastSeenAtUtc = c.LastSeenAtUtc ?? c.FirstSeenAtUtc,
                LastPurchaseAtUtc = c.LastPurchaseAtUtc ?? c.FirstSeenAtUtc
            })
            .ToListAsync(ct);
    }

    private static int ClampTake(int take)
    {
        if (take <= 0) return 10;
        if (take > 100) return 100;
        return take;
    }
}
