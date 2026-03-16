using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Application.Interfaces;
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
        if (!string.IsNullOrWhiteSpace(globalKey) && key == globalKey)
            return true;

        // Legacy global key sources
        string?[] legacySources = [
            Environment.GetEnvironmentVariable("WHATSAPP_ADMIN_KEY"),
            _config["WhatsApp:AdminKey"],
        ];
        foreach (var src in legacySources)
        {
            if (!string.IsNullOrWhiteSpace(src) && key == src.Trim())
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

            return result.ToString()?.Trim() == key;
        }
        catch
        {
            return false;
        }
    }

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
