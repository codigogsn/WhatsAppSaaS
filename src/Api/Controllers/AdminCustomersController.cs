using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Api.Auth;
using WhatsAppSaaS.Application.Services;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace Api.Controllers;

[ApiController]
[Route("api/admin/customers")]
[Authorize]
[EnableRateLimiting("admin")]
public class AdminCustomersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<AdminCustomersController> _logger;

    public AdminCustomersController(AppDbContext db, IConfiguration config, ILogger<AdminCustomersController> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
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
        // JWT users are scoped to their own business
        businessId = AdminAuth.ScopeBusinessId(User, businessId);

        if (!AdminAuth.IsAuthorized(User, Request, _config)) return Unauthorized();
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
                ? " AND (o.\"CustomerName\" ILIKE @search OR o.\"CustomerPhone\" ILIKE @search)"
                : "";

            var orderBy = sort switch
            {
                "orders" => "ORDER BY orders_count DESC",
                "spent" => "ORDER BY total_spent DESC",
                _ => "ORDER BY last_purchase DESC NULLS LAST"
            };

            var completedFilter = """CAST(o."CheckoutCompleted" AS TEXT) IN ('true','1','t')""";
            var completedFilter2 = """CAST(o2."CheckoutCompleted" AS TEXT) IN ('true','1','t')""";

            var sql = $"""
                SELECT
                    base.phone, base.orders_count, base.total_spent,
                    base.last_purchase, base.pref_delivery, base.pref_payment,
                    (SELECT o2."CustomerName"
                     FROM "Orders" o2
                     WHERE o2."CustomerPhone" = base.phone
                       AND {completedFilter2}
                       AND o2."CustomerName" IS NOT NULL AND o2."CustomerName" != ''
                       {(businessId.HasValue ? "AND CAST(o2.\"BusinessId\" AS TEXT) = @bid" : "")}
                     ORDER BY o2."CreatedAtUtc" DESC
                     LIMIT 1) AS name,
                    (SELECT oi."Name"
                     FROM "OrderItems" oi
                     INNER JOIN "Orders" o2 ON CAST(o2."Id" AS TEXT) = CAST(oi."OrderId" AS TEXT)
                     WHERE o2."CustomerPhone" = base.phone
                       AND {completedFilter2}
                       {(businessId.HasValue ? "AND CAST(o2.\"BusinessId\" AS TEXT) = @bid" : "")}
                     GROUP BY oi."Name"
                     ORDER BY SUM(oi."Quantity") DESC
                     LIMIT 1) AS fav_item
                FROM (
                    SELECT
                        o."CustomerPhone" AS phone,
                        COUNT(*) AS orders_count,
                        COALESCE(SUM(o."TotalAmount"), 0) AS total_spent,
                        MAX(o."CreatedAtUtc") AS last_purchase,
                        MODE() WITHIN GROUP (ORDER BY o."DeliveryType") AS pref_delivery,
                        MODE() WITHIN GROUP (ORDER BY o."PaymentMethod") AS pref_payment
                    FROM "Orders" o
                    WHERE o."CustomerPhone" IS NOT NULL AND o."CustomerPhone" != ''
                      AND {completedFilter}
                      {bizFilter}{searchFilter}
                    GROUP BY o."CustomerPhone"
                ) base
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
                    name = !string.IsNullOrWhiteSpace(name) ? WebhookProcessor.NormalizeDisplayName(name) : "N/A",
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
            _logger.LogError(ex, "Admin customers endpoint error");
            return StatusCode(500, new { error = $"CRM query failed: Unexpected server error" });
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

    /// <summary>
    /// POST /api/admin/customers/backfill-names
    /// Normalizes customer names in both Customers and Orders tables.
    /// Safe: only touches Name/CustomerName columns, formatting and casing only.
    /// </summary>
    [HttpPost("backfill-names")]
    public async Task<IActionResult> BackfillNames(CancellationToken ct)
    {
        // Cross-business mutation: restrict to global admin key only
        if (!IsAdmin()) return Unauthorized(new { error = "Global admin key required" });

        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            // Phase 1: Normalize Customers.Name
            int customersFixed = 0;
            using (var readCmd = conn.CreateCommand())
            {
                readCmd.CommandText = """SELECT "Id", "Name" FROM "Customers" WHERE "Name" IS NOT NULL AND "Name" != ''""";
                var updates = new List<(string id, string normalizedName)>();
                using var reader = await readCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var id = reader["Id"]?.ToString() ?? "";
                    var raw = reader["Name"]?.ToString() ?? "";
                    var normalized = WebhookProcessor.NormalizeDisplayName(raw);
                    if (normalized != raw)
                        updates.Add((id, normalized));
                }
                reader.Close();

                foreach (var (id, normalizedName) in updates)
                {
                    using var upCmd = conn.CreateCommand();
                    upCmd.CommandText = """UPDATE "Customers" SET "Name" = @name WHERE CAST("Id" AS TEXT) = @id""";
                    AddP(upCmd, "name", normalizedName);
                    AddP(upCmd, "id", id);
                    await upCmd.ExecuteNonQueryAsync(ct);
                    customersFixed++;
                }
            }

            // Phase 2: Normalize Orders.CustomerName
            int ordersFixed = 0;
            using (var readCmd = conn.CreateCommand())
            {
                readCmd.CommandText = """SELECT "Id", "CustomerName" FROM "Orders" WHERE "CustomerName" IS NOT NULL AND "CustomerName" != ''""";
                var updates = new List<(string id, string normalizedName)>();
                using var reader = await readCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var id = reader["Id"]?.ToString() ?? "";
                    var raw = reader["CustomerName"]?.ToString() ?? "";
                    var normalized = WebhookProcessor.NormalizeDisplayName(raw);
                    if (normalized != raw)
                        updates.Add((id, normalized));
                }
                reader.Close();

                foreach (var (id, normalizedName) in updates)
                {
                    using var upCmd = conn.CreateCommand();
                    upCmd.CommandText = """UPDATE "Orders" SET "CustomerName" = @name WHERE CAST("Id" AS TEXT) = @id""";
                    AddP(upCmd, "name", normalizedName);
                    AddP(upCmd, "id", id);
                    await upCmd.ExecuteNonQueryAsync(ct);
                    ordersFixed++;
                }
            }

            return Ok(new { customersFixed, ordersFixed });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin customers endpoint error");
            return StatusCode(500, new { error = $"Backfill failed: Unexpected server error" });
        }
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
