using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Application.Services;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace Api.Controllers;

/// <summary>
/// Dashboard analytics. The legacy summary endpoint uses raw ADO.NET because
/// production columns (Businesses.IsActive, Orders.CheckoutCompleted) are
/// integer, not boolean — EF queries throw type-mismatch errors.
/// </summary>
[ApiController]
[Route("admin/analytics")]
[EnableRateLimiting("admin")]
public class AdminAnalyticsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IAdminAnalyticsService _analytics;
    private readonly IConfiguration _config;

    public AdminAnalyticsController(AppDbContext db, IAdminAnalyticsService analytics, IConfiguration config)
    {
        _db = db;
        _analytics = analytics;
        _config = config;
    }

    // ── Auth helper: raw ADO.NET because Businesses.IsActive is integer ──
    private async Task<bool> IsAuthorizedAsync(Guid? businessId, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Admin-Key", out var hk) || string.IsNullOrWhiteSpace(hk))
            return false;

        var key = hk.ToString().Trim();

        // Global admin key — always authorized
        var globalKey = (_config["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY"))?.Trim();
        if (!string.IsNullOrWhiteSpace(globalKey) && SafeEquals(key, globalKey))
            return true;

        // Legacy global key sources
        string?[] legacySources = [
            Environment.GetEnvironmentVariable("WHATSAPP_ADMIN_KEY"),
            _config["WhatsApp:AdminKey"],
        ];
        foreach (var src in legacySources)
        {
            if (!string.IsNullOrWhiteSpace(src) && SafeEquals(key, src.Trim()))
                return true;
        }

        // Per-business key check (only if businessId provided)
        if (!businessId.HasValue) return false;

        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT "AdminKey" FROM "Businesses"
                WHERE "Id"::text = @bid AND "IsActive"::boolean = true
                LIMIT 1
            """;
            var p = cmd.CreateParameter();
            p.ParameterName = "bid";
            p.Value = businessId.Value.ToString();
            cmd.Parameters.Add(p);

            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is null or DBNull) return false;

            var dbKey = result.ToString()?.Trim() ?? "";
            return SafeEquals(key, dbKey);
        }
        catch
        {
            return false;
        }
    }

    private static bool SafeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    // ── Legacy summary — raw ADO.NET for production schema compatibility ──
    [HttpGet("/api/admin/analytics/summary")]
    public async Task<IActionResult> GetSummary([FromQuery] Guid? businessId, CancellationToken ct)
    {
        if (!await IsAuthorizedAsync(businessId, ct))
            return Unauthorized();

        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            // ── Orders metrics ──
            var bizFilter = businessId.HasValue ? """ AND "BusinessId" = @bid""" : "";

            using var ordCmd = conn.CreateCommand();
            ordCmd.CommandText = $"""
                SELECT
                    COUNT(*) AS total_orders,
                    COALESCE(SUM(CASE WHEN "CheckoutCompleted"::boolean = true THEN 1 ELSE 0 END), 0) AS completed_orders,
                    COALESCE(SUM(CASE WHEN "CheckoutCompleted"::boolean = true THEN COALESCE("TotalAmount", 0) ELSE 0 END), 0) AS total_revenue,
                    COALESCE(SUM(CASE WHEN "CheckoutCompleted"::boolean = true AND "CreatedAtUtc" >= @today THEN 1 ELSE 0 END), 0) AS orders_today,
                    COALESCE(SUM(CASE WHEN "CheckoutCompleted"::boolean = true AND "CreatedAtUtc" >= @today THEN COALESCE("TotalAmount", 0) ELSE 0 END), 0) AS revenue_today,
                    COALESCE(SUM(CASE WHEN "CheckoutCompleted"::boolean = true AND "CreatedAtUtc" >= @week THEN 1 ELSE 0 END), 0) AS orders_week,
                    COALESCE(SUM(CASE WHEN "PaymentProofMediaId" IS NOT NULL AND "PaymentProofMediaId" != '' AND "PaymentVerifiedAtUtc" IS NULL THEN 1 ELSE 0 END), 0) AS payments_pending
                FROM "Orders"
                WHERE 1=1{bizFilter}
            """;
            AddParam(ordCmd, "today", DateTime.UtcNow.Date);
            AddParam(ordCmd, "week", DateTime.UtcNow.AddDays(-7));
            if (businessId.HasValue) AddParam(ordCmd, "bid", businessId.Value);

            int totalOrders = 0, completedOrders = 0, ordersToday = 0, ordersWeek = 0, paymentsPending = 0;
            decimal totalRevenue = 0, revenueToday = 0;

            using (var reader = await ordCmd.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    totalOrders = Convert.ToInt32(reader["total_orders"]);
                    completedOrders = Convert.ToInt32(reader["completed_orders"]);
                    totalRevenue = Convert.ToDecimal(reader["total_revenue"]);
                    ordersToday = Convert.ToInt32(reader["orders_today"]);
                    revenueToday = Convert.ToDecimal(reader["revenue_today"]);
                    ordersWeek = Convert.ToInt32(reader["orders_week"]);
                    paymentsPending = Convert.ToInt32(reader["payments_pending"]);
                }
            }

            var avgTicket = completedOrders == 0 ? 0m : Math.Round(totalRevenue / completedOrders, 2);

            // ── Customer metrics ──
            using var custCmd = conn.CreateCommand();
            var custBizFilter = businessId.HasValue ? """ WHERE "BusinessId" = @bid""" : "";
            custCmd.CommandText = $"""
                SELECT
                    COUNT(*) AS total_customers,
                    COALESCE(SUM(CASE WHEN "OrdersCount" > 1 THEN 1 ELSE 0 END), 0) AS returning_customers
                FROM "Customers"{custBizFilter}
            """;
            if (businessId.HasValue) AddParam(custCmd, "bid", businessId.Value);

            int totalCustomers = 0, returningCustomers = 0;
            using (var reader = await custCmd.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    totalCustomers = Convert.ToInt32(reader["total_customers"]);
                    returningCustomers = Convert.ToInt32(reader["returning_customers"]);
                }
            }

            var returningPct = totalCustomers == 0 ? 0m : Math.Round((decimal)returningCustomers / totalCustomers * 100, 1);

            // ── Top selling item ──
            string? topItem = null;
            if (businessId.HasValue)
            {
                using var topCmd = conn.CreateCommand();
                topCmd.CommandText = """
                    SELECT oi."Name", SUM(oi."Quantity") AS total_qty
                    FROM "OrderItems" oi
                    JOIN "Orders" o ON o."Id" = oi."OrderId"
                    WHERE o."BusinessId" = @bid
                      AND oi."Name" IS NOT NULL AND oi."Name" != ''
                      AND oi."Quantity" > 0
                    GROUP BY oi."Name"
                    ORDER BY total_qty DESC
                    LIMIT 1
                """;
                AddParam(topCmd, "bid", businessId.Value);
                var topResult = await topCmd.ExecuteScalarAsync(ct);
                if (topResult is not null and not DBNull)
                    topItem = topResult.ToString()?.Trim().ToLowerInvariant();
            }

            return Ok(new
            {
                totalOrders,
                completedOrders,
                pendingOrders = Math.Max(0, totalOrders - completedOrders),
                totalRevenue,
                totalCustomers,
                uniqueCustomers = totalCustomers,
                averageTicket = avgTicket,
                ordersToday,
                revenueToday,
                ordersThisWeek = ordersWeek,
                returningCustomersPct = returningPct,
                topSellingItem = topItem,
                paymentsPendingVerification = paymentsPending
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Analytics summary failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    // ── Extended analytics — Phase A + B metrics for dashboard ──
    [HttpGet("/api/admin/analytics/extended")]
    public async Task<IActionResult> GetExtended([FromQuery] Guid? businessId, CancellationToken ct)
    {
        if (!await IsAuthorizedAsync(businessId, ct))
            return Unauthorized();

        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            var bizFilter = businessId.HasValue ? """ AND o."BusinessId" = @bid""" : "";
            var bizFilterShort = businessId.HasValue ? """ WHERE "BusinessId" = @bid""" : "";
            var bizFilterAnd = businessId.HasValue ? """ AND "BusinessId" = @bid""" : "";

            // ── 1) Peak Hour (last 7 days for robustness — today alone is often empty) ──
            string? peakHourLabel = null;
            int peakHourOrders = 0;
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    SELECT EXTRACT(HOUR FROM "CreatedAtUtc") AS hr, COUNT(*) AS cnt
                    FROM "Orders"
                    WHERE "CheckoutCompleted"::boolean = true
                      AND "CreatedAtUtc" >= @week{bizFilterAnd}
                    GROUP BY hr ORDER BY cnt DESC LIMIT 1
                """;
                AddParam(cmd, "week", DateTime.UtcNow.AddDays(-7));
                if (businessId.HasValue) AddParam(cmd, "bid", businessId.Value);
                using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    var hr = Convert.ToInt32(r["hr"]);
                    peakHourOrders = Convert.ToInt32(r["cnt"]);
                    peakHourLabel = $"{hr:00}:00 - {(hr + 1) % 24:00}:00";
                }
            }

            // ── 2+3) Best / Worst weekday by revenue (last 30 days) ──
            string? bestWeekday = null, worstWeekday = null;
            decimal bestWeekdayRevenue = 0, worstWeekdayRevenue = 0;
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    SELECT EXTRACT(DOW FROM "CreatedAtUtc") AS dow,
                           COALESCE(SUM(COALESCE("TotalAmount",0)),0) AS rev
                    FROM "Orders"
                    WHERE "CheckoutCompleted"::boolean = true
                      AND "CreatedAtUtc" >= @month{bizFilterAnd}
                    GROUP BY dow ORDER BY rev DESC
                """;
                AddParam(cmd, "month", DateTime.UtcNow.AddDays(-30));
                if (businessId.HasValue) AddParam(cmd, "bid", businessId.Value);
                var days = new List<(int Dow, decimal Rev)>();
                using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    days.Add((Convert.ToInt32(r["dow"]), Convert.ToDecimal(r["rev"])));
                if (days.Count > 0)
                {
                    bestWeekday = DowName(days[0].Dow);
                    bestWeekdayRevenue = days[0].Rev;
                    worstWeekday = DowName(days[^1].Dow);
                    worstWeekdayRevenue = days[^1].Rev;
                }
            }

            // ── 4) Avg items per order ──
            decimal avgItemsPerOrder = 0;
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    SELECT COALESCE(AVG(item_count), 0) AS avg_items
                    FROM (
                        SELECT o."Id", SUM(oi."Quantity") AS item_count
                        FROM "Orders" o
                        JOIN "OrderItems" oi ON oi."OrderId" = o."Id"
                        WHERE o."CheckoutCompleted"::boolean = true{bizFilter}
                        GROUP BY o."Id"
                    ) sub
                """;
                if (businessId.HasValue) AddParam(cmd, "bid", businessId.Value);
                var val = await cmd.ExecuteScalarAsync(ct);
                if (val is not null and not DBNull) avgItemsPerOrder = Math.Round(Convert.ToDecimal(val), 1);
            }

            // ── 5) Conversations started (last 30 days) ──
            // Use total orders created (any status) as proxy for order-intent conversations.
            // This is more reliable than ConversationStates count which includes
            // greetings-only sessions and gets purged by TTL.
            int conversationsStarted = 0;
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    SELECT COUNT(*) FROM "Orders"
                    WHERE "CreatedAtUtc" >= @month{bizFilterAnd}
                """;
                AddParam(cmd, "month", DateTime.UtcNow.AddDays(-30));
                if (businessId.HasValue) AddParam(cmd, "bid", businessId.Value);
                conversationsStarted = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            }

            // ── 6) Conversion rate ──
            // Formula: completed orders / total orders created (last 30d)
            // This measures "order fulfillment rate" — always 0-100%.
            // Total orders = all order records (intent expressed, checkout started).
            // Completed = CheckoutCompleted=true (order confirmed and finalized).
            int confirmedOrders30d = 0;
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    SELECT COUNT(*) FROM "Orders"
                    WHERE "CheckoutCompleted"::boolean = true
                      AND "CreatedAtUtc" >= @month{bizFilterAnd}
                """;
                AddParam(cmd, "month", DateTime.UtcNow.AddDays(-30));
                if (businessId.HasValue) AddParam(cmd, "bid", businessId.Value);
                confirmedOrders30d = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            }
            // Clamp to 0-100% — mathematically guaranteed since confirmed ≤ total
            var conversionRate = conversationsStarted == 0 ? 0m
                : Math.Min(100m, Math.Round((decimal)confirmedOrders30d / conversationsStarted * 100, 1));

            // ── 7) Bot vs Human handoff ──
            // Count active handoff conversations from ConversationStates JSON
            int handoffCount = 0;
            int totalConversations30d = 0;
            {
                using var cmd = conn.CreateCommand();
                var csBizFilter = businessId.HasValue ? """ AND "BusinessId" = @bid""" : "";
                cmd.CommandText = $"""
                    SELECT
                      COUNT(*) AS total,
                      COALESCE(SUM(CASE WHEN "StateJson" LIKE '%"humanHandoffRequested":true%'
                        OR "StateJson" LIKE '%"humanHandoffAtUtc":"%' THEN 1 ELSE 0 END), 0) AS handoffs
                    FROM "ConversationStates"
                    WHERE "UpdatedAtUtc" >= @month{csBizFilter}
                """;
                AddParam(cmd, "month", DateTime.UtcNow.AddDays(-30));
                if (businessId.HasValue) AddParam(cmd, "bid", businessId.Value);
                using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    totalConversations30d = Convert.ToInt32(r["total"]);
                    handoffCount = Convert.ToInt32(r["handoffs"]);
                }
            }
            int botResolved = Math.Max(0, totalConversations30d - handoffCount);
            var botPct = totalConversations30d == 0 ? 0m
                : Math.Min(100m, Math.Round((decimal)botResolved / totalConversations30d * 100, 1));
            var humanPct = totalConversations30d == 0 ? 0m
                : Math.Min(100m, Math.Round((decimal)handoffCount / totalConversations30d * 100, 1));

            // ── 8) New vs returning customers (last 30 days) ──
            // Uses actual Orders table to verify purchases, not just Customer metadata.
            // New = distinct customers whose FIRST completed order is within the 30d window.
            // Returning = distinct customers who had a completed order BEFORE the window
            //             AND also have a completed order WITHIN the window.
            int newCustomers30d = 0, returningCustomers30d = 0;
            {
                using var cmd = conn.CreateCommand();
                var month = DateTime.UtcNow.AddDays(-30);
                // Subquery: for each customer (by phone), find their earliest order
                // and check if they also ordered in this period.
                cmd.CommandText = $"""
                    WITH customer_orders AS (
                        SELECT "From",
                               MIN("CreatedAtUtc") AS first_order,
                               MAX("CreatedAtUtc") AS last_order,
                               COUNT(*) AS order_count
                        FROM "Orders"
                        WHERE "CheckoutCompleted"::boolean = true{bizFilterAnd}
                        GROUP BY "From"
                    )
                    SELECT
                      COALESCE(SUM(CASE WHEN first_order >= @month THEN 1 ELSE 0 END), 0) AS new_cust,
                      COALESCE(SUM(CASE WHEN first_order < @month AND last_order >= @month THEN 1 ELSE 0 END), 0) AS ret_cust
                    FROM customer_orders
                    WHERE last_order >= @month
                """;
                AddParam(cmd, "month", month);
                if (businessId.HasValue) AddParam(cmd, "bid", businessId.Value);
                using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    newCustomers30d = Convert.ToInt32(r["new_cust"]);
                    returningCustomers30d = Convert.ToInt32(r["ret_cust"]);
                }
            }

            // ── 9+10) Sales/Orders by hour (last 7 days) ──
            var salesByHour = new decimal[24];
            var ordersByHour = new int[24];
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    SELECT EXTRACT(HOUR FROM "CreatedAtUtc") AS hr,
                           COUNT(*) AS cnt,
                           COALESCE(SUM(COALESCE("TotalAmount",0)),0) AS rev
                    FROM "Orders"
                    WHERE "CheckoutCompleted"::boolean = true
                      AND "CreatedAtUtc" >= @week{bizFilterAnd}
                    GROUP BY hr ORDER BY hr
                """;
                AddParam(cmd, "week", DateTime.UtcNow.AddDays(-7));
                if (businessId.HasValue) AddParam(cmd, "bid", businessId.Value);
                using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var hr = Convert.ToInt32(r["hr"]);
                    if (hr >= 0 && hr < 24)
                    {
                        ordersByHour[hr] = Convert.ToInt32(r["cnt"]);
                        salesByHour[hr] = Convert.ToDecimal(r["rev"]);
                    }
                }
            }

            // ── 11) Top revenue product ──
            string? topRevenueProduct = null;
            decimal topRevenueProductAmount = 0;
            if (businessId.HasValue)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT oi."Name", COALESCE(SUM(oi."LineTotal"),0) AS rev
                    FROM "OrderItems" oi
                    JOIN "Orders" o ON o."Id" = oi."OrderId"
                    WHERE o."BusinessId" = @bid
                      AND o."CheckoutCompleted"::boolean = true
                      AND oi."Name" IS NOT NULL AND oi."Name" != ''
                    GROUP BY oi."Name" ORDER BY rev DESC LIMIT 1
                """;
                AddParam(cmd, "bid", businessId.Value);
                using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    topRevenueProduct = r.GetString(0);
                    topRevenueProductAmount = Convert.ToDecimal(r["rev"]);
                }
            }

            // ── 12) Top category by sales ──
            // OrderItems don't store category, so join through MenuItems→MenuCategories
            string? topCategory = null;
            decimal topCategoryRevenue = 0;
            if (businessId.HasValue)
            {
                using var cmd = conn.CreateCommand();
                // Best-effort: match OrderItem.Name to MenuItem.Name to get category
                cmd.CommandText = """
                    SELECT mc."Name" AS cat, COALESCE(SUM(oi."LineTotal"),0) AS rev
                    FROM "OrderItems" oi
                    JOIN "Orders" o ON o."Id" = oi."OrderId"
                    JOIN "MenuItems" mi ON LOWER(TRIM(mi."Name")) = LOWER(TRIM(oi."Name"))
                    JOIN "MenuCategories" mc ON mc."Id" = mi."CategoryId"
                    WHERE o."BusinessId" = @bid
                      AND o."CheckoutCompleted"::boolean = true
                      AND mc."BusinessId" = @bid
                    GROUP BY mc."Name" ORDER BY rev DESC LIMIT 1
                """;
                AddParam(cmd, "bid", businessId.Value);
                using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    topCategory = r.GetString(0);
                    topCategoryRevenue = Convert.ToDecimal(r["rev"]);
                }
            }

            // ── 13) Top 5 customers by revenue ──
            var topCustomers = new List<object>();
            if (businessId.HasValue)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT
                        base.phone,
                        base.orders_count,
                        base.total_spent,
                        (SELECT o2."CustomerName" FROM "Orders" o2
                         WHERE o2."CustomerPhone" = base.phone
                           AND CAST(o2."CheckoutCompleted" AS TEXT) IN ('true','1','t')
                           AND o2."CustomerName" IS NOT NULL AND o2."CustomerName" != ''
                         ORDER BY o2."CreatedAtUtc" DESC LIMIT 1) AS name
                    FROM (
                        SELECT
                            o."CustomerPhone" AS phone,
                            COUNT(*) AS orders_count,
                            COALESCE(SUM(o."TotalAmount"), 0) AS total_spent
                        FROM "Orders" o
                        WHERE o."CustomerPhone" IS NOT NULL AND o."CustomerPhone" != ''
                          AND CAST(o."CheckoutCompleted" AS TEXT) IN ('true','1','t')
                          AND CAST(o."BusinessId" AS TEXT) = @bid
                        GROUP BY o."CustomerPhone"
                    ) base
                    ORDER BY base.total_spent DESC
                    LIMIT 5
                """;
                AddParam(cmd, "bid", businessId.Value.ToString());
                using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    topCustomers.Add(new
                    {
                        name = r["name"] is DBNull ? (string?)null
                            : (!string.IsNullOrWhiteSpace(r["name"]?.ToString())
                                ? WebhookProcessor.NormalizeDisplayName(r["name"]!.ToString()!)
                                : r["name"]?.ToString()),
                        phone = r["phone"]?.ToString() ?? "",
                        orders = Convert.ToInt32(r["orders_count"]),
                        revenue = Convert.ToDecimal(r["total_spent"])
                    });
                }
            }

            return Ok(new
            {
                // Phase A
                peakHour = peakHourLabel,
                peakHourOrders,
                bestWeekday,
                bestWeekdayRevenue,
                worstWeekday,
                worstWeekdayRevenue,
                avgItemsPerOrder,
                conversationsStarted,
                conversionRate,
                botResolved,
                botResolvedPct = botPct,
                humanHandoffs = handoffCount,
                humanHandoffPct = humanPct,
                newCustomers = newCustomers30d,
                returningCustomers = returningCustomers30d,
                // Phase B
                salesByHour,
                ordersByHour,
                topRevenueProduct,
                topRevenueProductAmount,
                topCategory,
                topCategoryRevenue,
                topCustomers
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Extended analytics failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    private static string DowName(int dow) => dow switch
    {
        0 => "Domingo", 1 => "Lunes", 2 => "Martes", 3 => "Miércoles",
        4 => "Jueves", 5 => "Viernes", 6 => "Sábado", _ => "?"
    };

    // ── 1) Sales Analytics ──
    [HttpGet("sales")]
    public async Task<IActionResult> GetSales([FromQuery] Guid businessId, CancellationToken ct)
    {
        if (!await IsAuthorizedAsync(businessId, ct)) return Unauthorized();

        try
        {
            var result = await _analytics.GetSalesAsync(businessId, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Sales analytics failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    // ── 2) Product Analytics ──
    [HttpGet("products")]
    public async Task<IActionResult> GetProducts([FromQuery] Guid businessId, CancellationToken ct)
    {
        if (!await IsAuthorizedAsync(businessId, ct)) return Unauthorized();

        try
        {
            var result = await _analytics.GetProductAnalyticsAsync(businessId, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Product analytics failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    // ── 3) Conversion Analytics ──
    [HttpGet("conversion")]
    public async Task<IActionResult> GetConversion([FromQuery] Guid businessId, CancellationToken ct)
    {
        if (!await IsAuthorizedAsync(businessId, ct)) return Unauthorized();

        try
        {
            var result = await _analytics.GetConversionAsync(businessId, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Conversion analytics failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    // ── 4) Operational Analytics ──
    [HttpGet("operations")]
    public async Task<IActionResult> GetOperations([FromQuery] Guid businessId, CancellationToken ct)
    {
        if (!await IsAuthorizedAsync(businessId, ct)) return Unauthorized();

        try
        {
            var result = await _analytics.GetOperationalAsync(businessId, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Operational analytics failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    // ── 5) Business Intelligence ──
    [HttpGet("business")]
    public async Task<IActionResult> GetBusinessIntelligence([FromQuery] Guid businessId, CancellationToken ct)
    {
        if (!await IsAuthorizedAsync(businessId, ct)) return Unauthorized();

        try
        {
            var result = await _analytics.GetBusinessIntelligenceAsync(businessId, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Business intelligence failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    // ── 6) Restaurant Insights ──
    [HttpGet("restaurant")]
    public async Task<IActionResult> GetRestaurantInsights([FromQuery] Guid businessId, CancellationToken ct)
    {
        if (!await IsAuthorizedAsync(businessId, ct)) return Unauthorized();

        try
        {
            var result = await _analytics.GetRestaurantInsightsAsync(businessId, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Restaurant insights failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
