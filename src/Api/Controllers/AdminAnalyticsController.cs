using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Serilog;
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
    private readonly ILogger<AdminAnalyticsController> _logger;

    public AdminAnalyticsController(AppDbContext db, IAdminAnalyticsService analytics, IConfiguration config, ILogger<AdminAnalyticsController> logger)
    {
        _db = db;
        _analytics = analytics;
        _config = config;
        _logger = logger;
    }

    private Guid? GetJwtBusinessId()
    {
        var claim = User.FindFirstValue("businessId");
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    // ── Auth helper: raw ADO.NET because Businesses.IsActive is integer ──
    private async Task<bool> IsAuthorizedAsync(Guid? businessId, CancellationToken ct)
    {
        // JWT auth: any valid role scoped to their assigned business(es)
        var role = User.FindFirstValue(ClaimTypes.Role);

        // Founder has unrestricted access to any business
        if (role == "Founder")
            return true;

        if ((role is "Owner" or "Manager" or "Operator") && businessId.HasValue)
        {
            // Check single businessId claim
            var jwtBizId = GetJwtBusinessId();
            if (jwtBizId.HasValue && jwtBizId.Value == businessId.Value)
                return true;
            // Check multi-business businessIds claim
            var multiClaim = User.FindFirstValue("businessIds");
            if (!string.IsNullOrWhiteSpace(multiClaim))
            {
                var ids = multiClaim.Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var id in ids)
                    if (Guid.TryParse(id.Trim(), out var g) && g == businessId.Value)
                        return true;
            }
        }

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
        // Founder can view any business; other JWT users are scoped to their own
        var role = User.FindFirstValue(ClaimTypes.Role);
        var jwtBizId = GetJwtBusinessId();
        if (jwtBizId.HasValue && role != "Founder")
            businessId = jwtBizId.Value;

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
            _logger.LogError(ex, "Admin analytics endpoint error");
            return StatusCode(500, new { error = $"Analytics summary failed: Unexpected server error" });
        }
    }

    // ── Extended analytics — Phase A + B metrics for dashboard ──
    [HttpGet("/api/admin/analytics/extended")]
    public async Task<IActionResult> GetExtended([FromQuery] Guid? businessId, CancellationToken ct)
    {
        // Founder can view any business; other JWT users are scoped to their own
        var role = User.FindFirstValue(ClaimTypes.Role);
        var jwtBizId = GetJwtBusinessId();
        if (jwtBizId.HasValue && role != "Founder")
            businessId = jwtBizId.Value;

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
            _logger.LogError(ex, "Admin analytics endpoint error");
            return StatusCode(500, new { error = $"Extended analytics failed: Unexpected server error" });
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
            _logger.LogError(ex, "Admin analytics endpoint error");
            return StatusCode(500, new { error = $"Sales analytics failed: Unexpected server error" });
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
            _logger.LogError(ex, "Admin analytics endpoint error");
            return StatusCode(500, new { error = $"Product analytics failed: Unexpected server error" });
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
            _logger.LogError(ex, "Admin analytics endpoint error");
            return StatusCode(500, new { error = $"Conversion analytics failed: Unexpected server error" });
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
            _logger.LogError(ex, "Admin analytics endpoint error");
            return StatusCode(500, new { error = $"Operational analytics failed: Unexpected server error" });
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
            _logger.LogError(ex, "Admin analytics endpoint error");
            return StatusCode(500, new { error = $"Business intelligence failed: Unexpected server error" });
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
            _logger.LogError(ex, "Admin analytics endpoint error");
            return StatusCode(500, new { error = $"Restaurant insights failed: Unexpected server error" });
        }
    }

    // ── Founder Global Dashboard ──
    [HttpGet("/api/admin/analytics/founder-overview")]
    public async Task<IActionResult> GetFounderOverview(CancellationToken ct)
    {
        var role = User.FindFirstValue(ClaimTypes.Role);
        if (role != "Founder" && !IsGlobalAdminKey())
            return Unauthorized(new { error = "Founder or global admin key required" });

        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

            // ── Step 1: Load all businesses and classify (same logic as business selector) ──
            var allBiz = new List<(Guid Id, string Name, string PhoneNumberId, string? AccessToken, bool IsActive)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT "Id", "Name", "PhoneNumberId", "AccessToken", "IsActive"::boolean
                    FROM "Businesses"
                """;
                using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    allBiz.Add((
                        r.GetGuid(0),
                        r[1]?.ToString() ?? "",
                        r[2]?.ToString() ?? "",
                        r.IsDBNull(3) ? null : r[3]?.ToString(),
                        r.GetBoolean(4)
                    ));
                }
            }

            // Apply same junk classification as AdminBusinessesController.ClassifyBusiness
            string[] placeholders = ["Default Business", "Demo Restaurant", "Test", "Test Business"];
            bool IsJunk(string name, string pid, string? token)
            {
                var n = (name ?? "").Trim();
                var ph = (pid ?? "").Trim();
                bool placeholderName = string.IsNullOrWhiteSpace(n)
                    || placeholders.Any(p => n.Equals(p, StringComparison.OrdinalIgnoreCase))
                    || n.StartsWith("Test ", StringComparison.OrdinalIgnoreCase)
                    || n.StartsWith("Verify", StringComparison.OrdinalIgnoreCase);
                bool invalidPhone = string.IsNullOrWhiteSpace(ph) || !ph.All(char.IsDigit) || ph.Length < 5;
                bool noToken = string.IsNullOrWhiteSpace(token);
                return (placeholderName && invalidPhone) || (invalidPhone && noToken);
            }

            var realBizIds = new HashSet<Guid>();
            foreach (var b in allBiz)
            {
                if (b.IsActive && !IsJunk(b.Name, b.PhoneNumberId, b.AccessToken))
                    realBizIds.Add(b.Id);
            }

            var totalBiz = realBizIds.Count;
            var activeBiz = totalBiz; // all real businesses are already active-filtered
            Log.Information("FOUNDER OVERVIEW: {Total} total DB rows, {Real} real businesses: {Names}",
                allBiz.Count, totalBiz,
                string.Join(", ", allBiz.Where(b => realBizIds.Contains(b.Id)).Select(b => b.Name)));

            // ── Step 2: Scoped metrics (only for real businesses) ──
            int totalUsers = 0, totalOrders = 0, ordersToday = 0, totalCustomers = 0;
            int pendingPayments = 0, handoffCount = 0, bizWithOrdersToday = 0;
            decimal totalRevenue = 0m, revenueToday = 0m;

            // Helper: build IN clause params for a fresh command
            void AddBizParams(System.Data.Common.DbCommand c, string prefix, out string inSql)
            {
                var parts = new List<string>();
                var idx = 0;
                foreach (var id in realBizIds)
                {
                    var pn = $"{prefix}{idx++}";
                    parts.Add($"@{pn}");
                    AddParam(c, pn, id);
                }
                inSql = string.Join(",", parts);
            }

            if (totalBiz > 0)
            {
                using (var cmd = conn.CreateCommand())
                {
                    AddBizParams(cmd, "b", out var inSql);
                    cmd.CommandText = $"""SELECT COUNT(*) FROM "BusinessUsers" WHERE "IsActive"::boolean = true AND "BusinessId" IN ({inSql})""";
                    totalUsers = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
                }
                using (var cmd = conn.CreateCommand())
                {
                    AddBizParams(cmd, "b", out var inSql);
                    cmd.CommandText = $"""SELECT COUNT(DISTINCT "Id") FROM "Customers" WHERE "BusinessId" IN ({inSql})""";
                    totalCustomers = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
                }
                using (var cmd = conn.CreateCommand())
                {
                    AddBizParams(cmd, "b", out var inSql);
                    AddParam(cmd, "today", DateTime.UtcNow.Date);
                    cmd.CommandText = $"""
                        SELECT COUNT(*),
                            COALESCE(SUM(CASE WHEN "CheckoutCompleted"::boolean AND "CreatedAtUtc" >= @today THEN 1 ELSE 0 END), 0),
                            COALESCE(SUM(CASE WHEN "CheckoutCompleted"::boolean THEN COALESCE("TotalAmount",0) ELSE 0 END), 0),
                            COALESCE(SUM(CASE WHEN "CheckoutCompleted"::boolean AND "CreatedAtUtc" >= @today THEN COALESCE("TotalAmount",0) ELSE 0 END), 0),
                            COALESCE(SUM(CASE WHEN "PaymentProofMediaId" IS NOT NULL AND "PaymentProofMediaId" != '' AND "PaymentVerifiedAtUtc" IS NULL THEN 1 ELSE 0 END), 0)
                        FROM "Orders" WHERE "BusinessId" IN ({inSql})
                    """;
                    using var r = await cmd.ExecuteReaderAsync(ct);
                    if (await r.ReadAsync(ct))
                    {
                        totalOrders = r.GetInt32(0);
                        ordersToday = Convert.ToInt32(r[1]);
                        totalRevenue = Convert.ToDecimal(r[2]);
                        revenueToday = Convert.ToDecimal(r[3]);
                        pendingPayments = Convert.ToInt32(r[4]);
                    }
                }
                using (var cmd = conn.CreateCommand())
                {
                    AddBizParams(cmd, "b", out var inSql);
                    AddParam(cmd, "today", DateTime.UtcNow.Date);
                    cmd.CommandText = $"""SELECT COUNT(DISTINCT "BusinessId") FROM "Orders" WHERE "CreatedAtUtc" >= @today AND "BusinessId" IN ({inSql})""";
                    bizWithOrdersToday = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT COUNT(DISTINCT "ConversationId") FROM "ConversationStates"
                    WHERE "StateJson" LIKE '%"humanHandoffRequested":true%'
                       OR "StateJson" LIKE '%"humanHandoffRequested": true%'
                """;
                handoffCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            }

            // ── Step 3: Per-business breakdown (real businesses only) ──
            var businesses = new List<object>();
            if (totalBiz > 0)
            {
                using (var cmd = conn.CreateCommand())
                {
                    AddBizParams(cmd, "rb", out var inSql);
                    AddParam(cmd, "today", DateTime.UtcNow.Date);
                    cmd.CommandText = $"""
                        SELECT b."Id", b."Name", b."IsActive"::boolean,
                            COALESCE(s.order_count, 0), COALESCE(s.orders_today, 0),
                            COALESCE(s.revenue, 0), COALESCE(u.user_count, 0), b."CreatedAtUtc"
                        FROM "Businesses" b
                        LEFT JOIN (
                            SELECT "BusinessId", COUNT(*) AS order_count,
                                SUM(CASE WHEN "CreatedAtUtc" >= @today THEN 1 ELSE 0 END) AS orders_today,
                                SUM(CASE WHEN "CheckoutCompleted"::boolean THEN COALESCE("TotalAmount",0) ELSE 0 END) AS revenue
                            FROM "Orders" GROUP BY "BusinessId"
                        ) s ON s."BusinessId" = b."Id"
                        LEFT JOIN (
                            SELECT "BusinessId", COUNT(*) AS user_count FROM "BusinessUsers" WHERE "IsActive"::boolean = true GROUP BY "BusinessId"
                        ) u ON u."BusinessId" = b."Id"
                        WHERE b."Id" IN ({inSql})
                        ORDER BY COALESCE(s.revenue, 0) DESC
                    """;
                    using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        businesses.Add(new
                        {
                            id = r[0]?.ToString(),
                            name = r[1]?.ToString(),
                            isActive = r.GetBoolean(2),
                            ordersTotal = Convert.ToInt32(r[3]),
                            ordersToday = Convert.ToInt32(r[4]),
                            revenue = Convert.ToDecimal(r[5]),
                            users = Convert.ToInt32(r[6]),
                            createdAtUtc = r.IsDBNull(7) ? (DateTime?)null : Convert.ToDateTime(r[7])
                        });
                    }
                }
            }

            // ── Top product (scoped to real businesses) ──
            string? topProduct = null;
            if (totalBiz > 0)
            {
                using (var cmd = conn.CreateCommand())
                {
                    AddBizParams(cmd, "tp", out var inSql);
                    cmd.CommandText = $"""
                        SELECT oi."Name" FROM "OrderItems" oi
                        INNER JOIN "Orders" o ON o."Id" = oi."OrderId"
                        WHERE o."BusinessId" IN ({inSql})
                        GROUP BY oi."Name" ORDER BY SUM(oi."Quantity") DESC LIMIT 1
                    """;
                    var result = await cmd.ExecuteScalarAsync(ct);
                    topProduct = result?.ToString();
                }
            }

            var avgOrdersPerBiz = activeBiz > 0 ? Math.Round((decimal)totalOrders / activeBiz, 1) : 0;
            var avgRevenuePerBiz = activeBiz > 0 ? Math.Round(totalRevenue / activeBiz, 2) : 0;
            var topBiz = businesses.Count > 0 ? businesses[0] : null;
            var newestBiz = businesses.OrderByDescending(b => ((dynamic)b).createdAtUtc).FirstOrDefault();

            return Ok(new
            {
                totalBusinesses = totalBiz,
                activeBusinesses = activeBiz,
                totalUsers,
                totalOrders,
                ordersToday,
                totalRevenue,
                revenueToday,
                totalCustomers,
                pendingPayments,
                handoffCount,
                topProduct,
                avgOrdersPerBiz,
                avgRevenuePerBiz,
                bizWithOrdersToday,
                topBusiness = topBiz,
                newestBusiness = newestBiz,
                businesses
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin analytics endpoint error");
            return StatusCode(500, new { error = $"Founder overview failed: Unexpected server error" });
        }
    }

    private bool IsGlobalAdminKey()
    {
        var globalKey = (_config["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY"))?.Trim();
        if (string.IsNullOrWhiteSpace(globalKey)) return false;
        if (!Request.Headers.TryGetValue("X-Admin-Key", out var hk)) return false;
        return SafeEquals(hk.ToString().Trim(), globalKey);
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
