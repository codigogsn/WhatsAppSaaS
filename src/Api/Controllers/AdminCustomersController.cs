using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace Api.Controllers;

[ApiController]
[Route("api/admin/customers")]
[EnableRateLimiting("admin")]
public class AdminCustomersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public AdminCustomersController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    /// <summary>
    /// GET /api/admin/customers/crm?businessId=xxx&sort=recent&search=...&take=100
    /// Returns enriched customer CRM data with favorite item, delivery/payment preferences.
    /// </summary>
    [HttpGet("crm")]
    public async Task<IActionResult> GetCrm(
        [FromQuery] Guid? businessId = null,
        [FromQuery] string sort = "recent",
        [FromQuery] string? search = null,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        if (!IsAdmin()) return Unauthorized();
        if (take < 1) take = 1;
        if (take > 500) take = 500;

        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            // Step 1: Get base customer data with preferences from Orders
            var bizFilter = businessId.HasValue
                ? " AND CAST(o.\"BusinessId\" AS TEXT) = @bid"
                : "";

            var searchFilter = !string.IsNullOrWhiteSpace(search)
                ? " AND (o.\"CustomerName\" ILIKE @search OR o.\"CustomerPhone\" ILIKE @search OR o.\"From\" ILIKE @search)"
                : "";

            var orderBy = sort switch
            {
                "orders" => "ORDER BY orders_count DESC",
                "spent" => "ORDER BY total_spent DESC",
                _ => "ORDER BY last_purchase DESC NULLS LAST"
            };

            var sql = $"""
                SELECT
                    o."From" AS phone,
                    MIN(o."CustomerName") AS name,
                    COUNT(*) AS orders_count,
                    COALESCE(SUM(o."TotalAmount"), 0) AS total_spent,
                    MAX(o."CreatedAtUtc") AS last_purchase,
                    MODE() WITHIN GROUP (ORDER BY o."DeliveryType") AS pref_delivery,
                    MODE() WITHIN GROUP (ORDER BY o."PaymentMethod") AS pref_payment,
                    (SELECT oi2."Name"
                     FROM "OrderItems" oi2
                     INNER JOIN "Orders" o2 ON o2."Id" = oi2."OrderId"
                     WHERE o2."From" = o."From"
                       {(businessId.HasValue ? "AND CAST(o2.\"BusinessId\" AS TEXT) = @bid" : "")}
                     GROUP BY oi2."Name"
                     ORDER BY SUM(oi2."Quantity") DESC
                     LIMIT 1) AS fav_item
                FROM "Orders" o
                WHERE o."From" IS NOT NULL AND o."From" != ''
                  AND o."CheckoutCompleted" = true
                  {bizFilter}{searchFilter}
                GROUP BY o."From"
                {orderBy}
                LIMIT @take
            """;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            AddP(cmd, "take", take);
            if (businessId.HasValue)
                AddP(cmd, "bid", businessId.Value.ToString());
            if (!string.IsNullOrWhiteSpace(search))
                AddP(cmd, "search", $"%{search.Trim()}%");

            var customers = new List<object>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var phone = reader["phone"]?.ToString() ?? "";
                var name = reader["name"] is DBNull ? null : reader["name"]?.ToString();
                var ordersCount = Convert.ToInt32(reader["orders_count"]);
                var totalSpent = reader["total_spent"] is DBNull ? 0m : Convert.ToDecimal(reader["total_spent"]);
                var lastPurchase = reader["last_purchase"] is DBNull ? (DateTime?)null
                    : Convert.ToDateTime(reader["last_purchase"]);
                var prefDelivery = reader["pref_delivery"] is DBNull ? null : reader["pref_delivery"]?.ToString();
                var prefPayment = reader["pref_payment"] is DBNull ? null : reader["pref_payment"]?.ToString();
                var favItem = reader["fav_item"] is DBNull ? null : reader["fav_item"]?.ToString();

                customers.Add(new
                {
                    phone,
                    name = name ?? "N/A",
                    ordersCount,
                    totalSpent,
                    lastPurchase,
                    favoriteItem = favItem ?? "N/A",
                    preferredDelivery = NormalizePreference(prefDelivery, "delivery", "pickup"),
                    preferredPayment = NormalizePaymentPref(prefPayment)
                });
            }

            return Ok(new { total = customers.Count, customers });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"CRM query failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    private static string NormalizePreference(string? value, params string[] known)
    {
        if (string.IsNullOrWhiteSpace(value)) return "N/A";
        foreach (var k in known)
            if (value.Equals(k, StringComparison.OrdinalIgnoreCase)) return k;
        return value;
    }

    private static string NormalizePaymentPref(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "N/A";
        return value.ToLowerInvariant() switch
        {
            "efectivo" => "efectivo",
            "pago_movil" => "pago_movil",
            "zelle" => "zelle",
            "divisas" => "divisas",
            _ => value
        };
    }

    private static void AddP(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private bool IsAdmin()
    {
        var adminKey = _config["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY");
        if (string.IsNullOrWhiteSpace(adminKey)) return false;
        if (!Request.Headers.TryGetValue("X-Admin-Key", out var headerKey)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(headerKey.ToString()),
            Encoding.UTF8.GetBytes(adminKey));
    }
}
