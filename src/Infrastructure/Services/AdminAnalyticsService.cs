using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

    // ── Legacy summary (unscoped) ──

    public async Task<AnalyticsSummaryDto> GetSummaryAsync(CancellationToken ct)
    {
        var totalOrdersTask = _db.Orders.AsNoTracking().CountAsync(ct);
        var completedOrdersTask = _db.Orders.AsNoTracking().CountAsync(o => o.CheckoutCompleted, ct);
        var totalCustomersTask = _db.Customers.AsNoTracking().CountAsync(ct);

        await Task.WhenAll(totalOrdersTask, completedOrdersTask, totalCustomersTask);

        var totalOrders = totalOrdersTask.Result;
        var completedOrders = completedOrdersTask.Result;

        var revenueRows = await _db.Orders
            .AsNoTracking()
            .Where(o => o.CheckoutCompleted)
            .Select(o => o.TotalAmount)
            .ToListAsync(ct);
        var totalRevenue = revenueRows.Sum(t => t ?? 0m);

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

        var rows = await _db.OrderItems
            .AsNoTracking()
            .Select(oi => new { oi.Name, oi.Quantity, oi.UnitPrice })
            .ToListAsync(ct);

        return rows
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && x.Quantity > 0)
            .GroupBy(x => x.Name!.Trim().ToLowerInvariant())
            .Select(g => new TopProductDto
            {
                Name = g.Key,
                TotalQuantity = g.Sum(x => x.Quantity),
                TotalRevenue = g.Sum(x => (x.UnitPrice ?? 0m) * x.Quantity)
            })
            .OrderByDescending(x => x.TotalQuantity)
            .ThenByDescending(x => x.TotalRevenue)
            .Take(take)
            .ToList();
    }

    public async Task<List<CustomerAnalyticsDto>> GetCustomersAsync(int take, CancellationToken ct)
    {
        take = ClampTake(take);

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

    // ── 1) Sales Analytics (scoped by BusinessId) ──

    public async Task<SalesAnalyticsDto> GetSalesAsync(Guid businessId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var todayStart = now.Date;
        var last7 = now.AddDays(-7);
        var last30 = now.AddDays(-30);

        // Fetch completed orders for this business in the last 30 days (covers all windows)
        var orders = await _db.Orders
            .AsNoTracking()
            .Where(o => o.BusinessId == businessId && o.CheckoutCompleted && o.CreatedAtUtc >= last30)
            .Select(o => new { o.TotalAmount, o.CreatedAtUtc })
            .ToListAsync(ct);

        // All-time stats (client-side Sum to avoid SQLite decimal limitation)
        var allTimeRows = await _db.Orders
            .AsNoTracking()
            .Where(o => o.BusinessId == businessId && o.CheckoutCompleted)
            .Select(o => o.TotalAmount)
            .ToListAsync(ct);

        var allTimeCount = allTimeRows.Count;
        var allTimeRevenue = allTimeRows.Sum(t => t ?? 0m);

        var salesToday = orders.Where(o => o.CreatedAtUtc >= todayStart).Sum(o => o.TotalAmount ?? 0m);
        var sales7 = orders.Where(o => o.CreatedAtUtc >= last7).Sum(o => o.TotalAmount ?? 0m);
        var sales30 = orders.Sum(o => o.TotalAmount ?? 0m);

        var avgTicket = allTimeCount == 0 ? 0m : allTimeRevenue / allTimeCount;

        // Sales by hour (last 30 days)
        var salesByHour = new Dictionary<int, decimal>();
        for (var h = 0; h < 24; h++) salesByHour[h] = 0m;
        foreach (var o in orders)
        {
            var h = o.CreatedAtUtc.Hour;
            salesByHour[h] += o.TotalAmount ?? 0m;
        }

        // Sales by day of week (last 30 days)
        var dayNames = new[] { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
        var salesByDay = dayNames.ToDictionary(d => d, _ => 0m);
        foreach (var o in orders)
        {
            var day = dayNames[(int)o.CreatedAtUtc.DayOfWeek];
            salesByDay[day] += o.TotalAmount ?? 0m;
        }

        return new SalesAnalyticsDto
        {
            SalesToday = salesToday,
            SalesLast7Days = sales7,
            SalesLast30Days = sales30,
            AverageTicket = Math.Round(avgTicket, 2),
            OrdersCount = allTimeCount,
            SalesByHour = salesByHour,
            SalesByDayOfWeek = salesByDay
        };
    }

    // ── 2) Product Analytics (scoped by BusinessId) ──

    public async Task<ProductAnalyticsDto> GetProductAnalyticsAsync(Guid businessId, CancellationToken ct)
    {
        var rows = await _db.OrderItems
            .AsNoTracking()
            .Where(oi => oi.Order != null && oi.Order.BusinessId == businessId)
            .Select(oi => new { oi.Name, oi.Quantity, oi.UnitPrice, oi.OrderId })
            .ToListAsync(ct);

        var totalOrders = rows.Select(r => r.OrderId).Distinct().Count();
        var totalItems = rows.Sum(r => r.Quantity);
        var avgQtyPerOrder = totalOrders == 0 ? 0m : (decimal)totalItems / totalOrders;

        var grouped = rows
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && x.Quantity > 0)
            .GroupBy(x => x.Name!.Trim().ToLowerInvariant())
            .Select(g => new ProductSalesEntry
            {
                Name = g.Key,
                TotalQuantity = g.Sum(x => x.Quantity),
                TotalRevenue = g.Sum(x => (x.UnitPrice ?? 0m) * x.Quantity),
                OrderCount = g.Select(x => x.OrderId).Distinct().Count()
            })
            .ToList();

        var topProducts = grouped
            .OrderByDescending(x => x.TotalQuantity)
            .ThenByDescending(x => x.TotalRevenue)
            .Take(10)
            .ToList();

        var leastOrdered = grouped
            .Where(x => x.TotalQuantity > 0)
            .OrderBy(x => x.TotalQuantity)
            .ThenBy(x => x.TotalRevenue)
            .Take(10)
            .ToList();

        return new ProductAnalyticsDto
        {
            TopProducts = topProducts,
            LeastOrderedProducts = leastOrdered,
            ProductSalesVolume = grouped.OrderByDescending(x => x.TotalRevenue).ToList(),
            AverageQuantityPerOrder = Math.Round(avgQtyPerOrder, 2)
        };
    }

    // ── 3) Conversion Analytics (scoped by BusinessId) ──

    public async Task<ConversionAnalyticsDto> GetConversionAsync(Guid businessId, CancellationToken ct)
    {
        // Messages received = ProcessedMessages for conversations belonging to this business
        var messagesReceived = await _db.ProcessedMessages
            .AsNoTracking()
            .CountAsync(pm => pm.Conversation != null && pm.Conversation.BusinessId == businessId, ct);

        // Conversations started = ConversationStates for this business
        var conversationsStarted = await _db.ConversationStates
            .AsNoTracking()
            .CountAsync(cs => cs.BusinessId == businessId, ct);

        // Parse state JSON to find checkout/completion stats
        var states = await _db.ConversationStates
            .AsNoTracking()
            .Where(cs => cs.BusinessId == businessId)
            .Select(cs => cs.StateJson)
            .ToListAsync(ct);

        var checkoutStarted = 0;
        var checkoutCompleted = 0;
        foreach (var json in states)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("checkoutFormSent", out var cfs) && cfs.GetBoolean())
                    checkoutStarted++;
            }
            catch { /* corrupt JSON, skip */ }
        }

        // Orders created = orders for this business
        var ordersCreated = await _db.Orders
            .AsNoTracking()
            .CountAsync(o => o.BusinessId == businessId, ct);

        checkoutCompleted = await _db.Orders
            .AsNoTracking()
            .CountAsync(o => o.BusinessId == businessId && o.CheckoutCompleted, ct);

        // Also count checkoutStarted from orders that had form sent
        var orderCheckoutStarted = await _db.Orders
            .AsNoTracking()
            .CountAsync(o => o.BusinessId == businessId && o.CheckoutFormSent, ct);
        checkoutStarted = Math.Max(checkoutStarted, orderCheckoutStarted);

        var abandoned = Math.Max(0, conversationsStarted - ordersCreated);
        var conversionRate = conversationsStarted == 0 ? 0m : Math.Round((decimal)ordersCreated / conversationsStarted * 100, 2);
        var abandonmentRate = conversationsStarted == 0 ? 0m : Math.Round((decimal)abandoned / conversationsStarted * 100, 2);

        return new ConversionAnalyticsDto
        {
            MessagesReceived = messagesReceived,
            ConversationsStarted = conversationsStarted,
            CheckoutStarted = checkoutStarted,
            CheckoutCompleted = checkoutCompleted,
            OrdersCreated = ordersCreated,
            AbandonedConversations = abandoned,
            ConversionRate = conversionRate,
            AbandonmentRate = abandonmentRate
        };
    }

    // ── 4) Operational Analytics (scoped by BusinessId) ──

    public async Task<OperationalAnalyticsDto> GetOperationalAsync(Guid businessId, CancellationToken ct)
    {
        var orders = await _db.Orders
            .AsNoTracking()
            .Where(o => o.BusinessId == businessId && o.CheckoutCompleted)
            .Select(o => new
            {
                o.CreatedAtUtc,
                o.AcceptedAtUtc,
                o.PreparingAtUtc,
                o.DeliveredAtUtc,
                o.DeliveryType,
                o.Status
            })
            .ToListAsync(ct);

        // Average preparation time: AcceptedAt -> PreparingAt (or CreatedAt -> PreparingAt if no AcceptedAt)
        var prepTimes = orders
            .Where(o => o.PreparingAtUtc.HasValue)
            .Select(o =>
            {
                var start = o.AcceptedAtUtc ?? o.CreatedAtUtc;
                return (o.PreparingAtUtc!.Value - start).TotalMinutes;
            })
            .Where(m => m > 0)
            .ToList();

        var avgPrepTime = prepTimes.Count > 0 ? Math.Round(prepTimes.Average(), 1) : (double?)null;

        // Average delivery time: PreparingAt -> DeliveredAt (or AcceptedAt -> DeliveredAt)
        var deliveryTimes = orders
            .Where(o => o.DeliveredAtUtc.HasValue)
            .Select(o =>
            {
                var start = o.PreparingAtUtc ?? o.AcceptedAtUtc ?? o.CreatedAtUtc;
                return (o.DeliveredAtUtc!.Value - start).TotalMinutes;
            })
            .Where(m => m > 0)
            .ToList();

        var avgDeliveryTime = deliveryTimes.Count > 0 ? Math.Round(deliveryTimes.Average(), 1) : (double?)null;

        // Orders by hour
        var ordersByHour = new Dictionary<int, int>();
        for (var h = 0; h < 24; h++) ordersByHour[h] = 0;
        foreach (var o in orders)
            ordersByHour[o.CreatedAtUtc.Hour]++;

        // Peak hours: top 3 hours by order volume
        var peakHours = ordersByHour
            .OrderByDescending(kv => kv.Value)
            .Take(3)
            .ToDictionary(kv => $"{kv.Key:D2}:00", kv => kv.Value);

        // Orders by delivery type
        var ordersByDeliveryType = orders
            .GroupBy(o => o.DeliveryType ?? "unknown")
            .ToDictionary(g => g.Key, g => g.Count());

        // Orders by status
        var ordersByStatus = orders
            .GroupBy(o => o.Status ?? "unknown")
            .ToDictionary(g => g.Key, g => g.Count());

        return new OperationalAnalyticsDto
        {
            AveragePreparationTimeMinutes = avgPrepTime,
            AverageDeliveryTimeMinutes = avgDeliveryTime,
            OrdersByHour = ordersByHour,
            PeakHours = peakHours,
            OrdersByDeliveryType = ordersByDeliveryType,
            OrdersByStatus = ordersByStatus
        };
    }

    // ── 5) Business Intelligence (scoped by BusinessId) ──

    public async Task<BusinessIntelligenceDto> GetBusinessIntelligenceAsync(Guid businessId, CancellationToken ct)
    {
        var customers = await _db.Customers
            .AsNoTracking()
            .Where(c => c.BusinessId == businessId)
            .Select(c => new { c.PhoneE164, c.Name, c.TotalSpent, c.OrdersCount })
            .ToListAsync(ct);

        var totalCustomers = customers.Count;
        var repeatCustomers = customers.Count(c => c.OrdersCount > 1);
        var totalOrders = customers.Sum(c => c.OrdersCount);
        var totalRevenue = customers.Sum(c => c.TotalSpent);

        var clv = totalCustomers == 0 ? 0m : Math.Round(totalRevenue / totalCustomers, 2);
        var repeatRate = totalCustomers == 0 ? 0m : Math.Round((decimal)repeatCustomers / totalCustomers * 100, 2);
        var ordersPerCustomer = totalCustomers == 0 ? 0m : Math.Round((decimal)totalOrders / totalCustomers, 2);

        var topCustomers = customers
            .OrderByDescending(c => c.TotalSpent)
            .Take(10)
            .Select(c => new TopCustomerDto
            {
                PhoneE164 = c.PhoneE164,
                Name = c.Name,
                TotalSpent = c.TotalSpent,
                OrdersCount = c.OrdersCount
            })
            .ToList();

        // Revenue by payment method
        var orderPayments = await _db.Orders
            .AsNoTracking()
            .Where(o => o.BusinessId == businessId && o.CheckoutCompleted)
            .Select(o => new { o.PaymentMethod, o.TotalAmount })
            .ToListAsync(ct);

        var revenueByPayment = orderPayments
            .GroupBy(o => o.PaymentMethod ?? "unknown")
            .ToDictionary(g => g.Key, g => g.Sum(o => o.TotalAmount ?? 0m));

        return new BusinessIntelligenceDto
        {
            CustomerLifetimeValue = clv,
            RepeatCustomerRate = repeatRate,
            OrdersPerCustomer = ordersPerCustomer,
            TopCustomers = topCustomers,
            RevenueByPaymentMethod = revenueByPayment,
            TotalCustomers = totalCustomers,
            RepeatCustomers = repeatCustomers
        };
    }

    // ── 6) Restaurant Insights (scoped by BusinessId) ──

    public async Task<RestaurantInsightsDto> GetRestaurantInsightsAsync(Guid businessId, CancellationToken ct)
    {
        var orders = await _db.Orders
            .AsNoTracking()
            .Where(o => o.BusinessId == businessId && o.CheckoutCompleted)
            .OrderBy(o => o.CreatedAtUtc)
            .Select(o => new { o.CreatedAtUtc })
            .ToListAsync(ct);

        // Best selling hours
        var ordersByHour = new Dictionary<int, int>();
        for (var h = 0; h < 24; h++) ordersByHour[h] = 0;
        foreach (var o in orders)
            ordersByHour[o.CreatedAtUtc.Hour]++;

        // Slow hours: hours with zero orders
        var slowHours = ordersByHour
            .Where(kv => kv.Value == 0)
            .Select(kv => kv.Key)
            .OrderBy(h => h)
            .ToList();

        // Average order interval
        double? avgInterval = null;
        if (orders.Count > 1)
        {
            var intervals = new List<double>();
            for (var i = 1; i < orders.Count; i++)
                intervals.Add((orders[i].CreatedAtUtc - orders[i - 1].CreatedAtUtc).TotalMinutes);

            avgInterval = intervals.Count > 0 ? Math.Round(intervals.Average(), 1) : null;
        }

        // Most popular items
        var items = await _db.OrderItems
            .AsNoTracking()
            .Where(oi => oi.Order != null && oi.Order.BusinessId == businessId)
            .Select(oi => new { oi.Name, oi.Quantity, oi.UnitPrice, oi.OrderId })
            .ToListAsync(ct);

        var mostPopular = items
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) && x.Quantity > 0)
            .GroupBy(x => x.Name!.Trim().ToLowerInvariant())
            .Select(g => new ProductSalesEntry
            {
                Name = g.Key,
                TotalQuantity = g.Sum(x => x.Quantity),
                TotalRevenue = g.Sum(x => (x.UnitPrice ?? 0m) * x.Quantity),
                OrderCount = g.Select(x => x.OrderId).Distinct().Count()
            })
            .OrderByDescending(x => x.TotalQuantity)
            .Take(10)
            .ToList();

        return new RestaurantInsightsDto
        {
            BestSellingHours = ordersByHour,
            MostPopularItems = mostPopular,
            SlowHours = slowHours,
            AverageOrderIntervalMinutes = avgInterval
        };
    }

    private static int ClampTake(int take)
    {
        if (take <= 0) return 10;
        if (take > 100) return 100;
        return take;
    }
}
