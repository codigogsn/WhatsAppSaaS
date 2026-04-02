using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Infrastructure.Services;

public sealed class BusinessInsightsService : IBusinessInsightsService
{
    private readonly AppDbContext _db;
    private readonly ILogger<BusinessInsightsService> _logger;

    public BusinessInsightsService(AppDbContext db, ILogger<BusinessInsightsService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<BusinessInsightsResponse> GetInsightsAsync(Guid businessId, int windowDays, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var windowStart = now.AddDays(-windowDays);

        // ── 1. Orders in window (completed only) ──
        var orders = await _db.Orders.AsNoTracking()
            .Where(o => o.BusinessId == businessId && o.CheckoutCompleted && o.CreatedAtUtc >= windowStart)
            .OrderByDescending(o => o.CreatedAtUtc)
            .Select(o => new
            {
                o.Id, o.TotalAmount, o.CreatedAtUtc, o.PaymentProofMediaId,
                o.PaymentVerifiedAtUtc, o.From
            })
            .Take(50_000)
            .ToListAsync(ct);

        var completedOrders = orders.Count;
        var totalRevenue = orders.Sum(o => o.TotalAmount ?? 0m);
        var avgTicket = completedOrders > 0 ? Math.Round(totalRevenue / completedOrders, 2) : 0m;

        // ── 2. Items in window ──
        var orderIds = orders.Select(o => o.Id).ToHashSet();
        var items = await _db.OrderItems.AsNoTracking()
            .Where(oi => oi.OrderId != Guid.Empty && orderIds.Contains(oi.OrderId))
            .OrderBy(oi => oi.Id)
            .Select(oi => new { oi.Name, oi.Quantity, oi.UnitPrice })
            .Take(50_000)
            .ToListAsync(ct);

        var totalItems = items.Sum(i => i.Quantity);
        var avgItemsPerOrder = completedOrders > 0 ? Math.Round((decimal)totalItems / completedOrders, 2) : 0m;

        // ── 3. Product performance ──
        var productGroups = items
            .Where(i => !string.IsNullOrWhiteSpace(i.Name) && i.Quantity > 0)
            .GroupBy(i => i.Name!.Trim().ToLowerInvariant())
            .Select(g => new ItemPerformance
            {
                Name = g.Key,
                TotalQuantity = g.Sum(x => x.Quantity),
                TotalRevenue = g.Sum(x => (x.UnitPrice ?? 0m) * x.Quantity)
            })
            .ToList();

        var topSelling = productGroups.OrderByDescending(p => p.TotalQuantity).Take(10).ToList();
        var lowPerforming = productGroups.Where(p => p.TotalQuantity > 0)
            .OrderBy(p => p.TotalQuantity).Take(5).ToList();

        // ── 4. Peak hours ──
        var hourCounts = new int[24];
        foreach (var o in orders)
            hourCounts[o.CreatedAtUtc.Hour]++;

        var peakHours = hourCounts
            .Select((count, hour) => new HourVolume { Hour = hour, OrderCount = count })
            .Where(h => h.OrderCount > 0)
            .OrderByDescending(h => h.OrderCount)
            .Take(5)
            .ToList();

        // ── 5. Repeat customer rate ──
        var customers = await _db.Customers.AsNoTracking()
            .Where(c => c.BusinessId == businessId)
            .OrderBy(c => c.Id)
            .Select(c => new { c.OrdersCount })
            .Take(50_000)
            .ToListAsync(ct);

        var totalCustomers = customers.Count;
        var repeatCustomers = customers.Count(c => c.OrdersCount > 1);
        var repeatRate = totalCustomers > 0
            ? Math.Round((decimal)repeatCustomers / totalCustomers * 100, 1) : 0m;

        // ── 6. Conversion rate ──
        var conversationsStarted = await _db.ConversationStates.AsNoTracking()
            .CountAsync(cs => cs.BusinessId == businessId && cs.UpdatedAtUtc >= windowStart, ct);

        var conversionRate = conversationsStarted > 0
            ? Math.Round((decimal)completedOrders / conversationsStarted * 100, 1) : 0m;

        // ── 7. Pending payments ──
        var pendingPayments = orders.Count(o =>
            !string.IsNullOrWhiteSpace(o.PaymentProofMediaId) && o.PaymentVerifiedAtUtc == null);

        // ── 8. Rule-based insights ──
        var insights = new List<InsightEntry>();
        var recommendations = new List<string>();
        var alerts = new List<string>();

        // Minimum-data guard: skip generating insights for very new businesses
        // (metrics + top items still returned, only alerts/insights/recs gated)
        var hasEnoughData = completedOrders >= 5;

        if (hasEnoughData)
        {
        // Peak hours insight
        if (peakHours.Count > 0)
        {
            var top = peakHours[0];
            insights.Add(new InsightEntry
            {
                Type = "peak_hours",
                Message = $"Highest order volume at {top.Hour:D2}:00 with {top.OrderCount} orders",
                Severity = "info"
            });
        }

        // Dominant product
        if (topSelling.Count > 0)
        {
            insights.Add(new InsightEntry
            {
                Type = "dominant_product",
                Message = $"Top seller: {topSelling[0].Name} ({topSelling[0].TotalQuantity} units)",
                Severity = "info"
            });
        }

        // Low performing product
        if (lowPerforming.Count > 0 && productGroups.Count >= 3)
        {
            insights.Add(new InsightEntry
            {
                Type = "low_performing_product",
                Message = $"Lowest seller: {lowPerforming[0].Name} ({lowPerforming[0].TotalQuantity} units)",
                Severity = "warning"
            });
            recommendations.Add($"Consider promoting or updating '{lowPerforming[0].Name}' — lowest demand in the last {windowDays} days.");
        }

        // Repeat rate insights
        if (repeatRate < 15m && totalCustomers >= 10)
        {
            insights.Add(new InsightEntry
            {
                Type = "low_repeat_rate",
                Message = $"Repeat customer rate is {repeatRate}% — below 15% threshold",
                Severity = "warning"
            });
            recommendations.Add("Consider loyalty incentives or follow-up messages to increase repeat orders.");
        }
        else if (repeatRate >= 40m && totalCustomers >= 10)
        {
            insights.Add(new InsightEntry
            {
                Type = "strong_repeat_rate",
                Message = $"Strong repeat customer rate: {repeatRate}%",
                Severity = "info"
            });
        }

        // Average ticket insight
        if (avgTicket < 5m && completedOrders >= 10)
        {
            insights.Add(new InsightEntry
            {
                Type = "low_average_ticket",
                Message = $"Average ticket is ${avgTicket} — consider upselling or combos",
                Severity = "warning"
            });
            recommendations.Add("Add combo deals or suggest add-ons to increase average order value.");
        }

        // Pending payment risk
        if (pendingPayments >= 3)
        {
            insights.Add(new InsightEntry
            {
                Type = "pending_payment_risk",
                Message = $"{pendingPayments} orders have unverified payment proofs",
                Severity = "critical"
            });
            alerts.Add($"Action required: {pendingPayments} payment proofs awaiting verification.");
        }

        // Low conversion
        if (conversionRate < 10m && conversationsStarted >= 20)
        {
            insights.Add(new InsightEntry
            {
                Type = "low_conversion_rate",
                Message = $"Conversation-to-order conversion is {conversionRate}% — below 10%",
                Severity = "warning"
            });
            recommendations.Add("Review menu pricing and bot greeting to improve conversion from conversations to orders.");
        }
        } // end hasEnoughData guard

        _logger.LogInformation(
            "INSIGHTS: generated for business {BusinessId} window={WindowDays}d orders={Orders} customers={Customers}",
            businessId, windowDays, completedOrders, totalCustomers);

        return new BusinessInsightsResponse
        {
            BusinessId = businessId,
            WindowDays = windowDays,
            GeneratedAtUtc = now,
            Metrics = new InsightsMetrics
            {
                CompletedOrders = completedOrders,
                AverageTicket = avgTicket,
                AverageItemsPerOrder = avgItemsPerOrder,
                RepeatCustomerRate = repeatRate,
                ConversionRate = conversionRate,
                PendingPayments = pendingPayments
            },
            TopSellingItems = topSelling,
            LowPerformingItems = lowPerforming,
            PeakHours = peakHours,
            Insights = insights,
            Recommendations = recommendations,
            Alerts = alerts
        };
    }
}
