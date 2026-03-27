using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace Api.Controllers;

/// <summary>
/// CSV exports. Uses raw ADO.NET because production Businesses.IsActive is
/// integer, not boolean — EF queries throw InvalidCastException.
/// </summary>
[ApiController]
[Route("api/admin/exports")]
[EnableRateLimiting("admin")]
public class AdminExportsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<AdminExportsController> _logger;

    public AdminExportsController(AppDbContext db, IConfiguration config, ILogger<AdminExportsController> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    private Guid? GetJwtBusinessId()
    {
        var claim = User.FindFirstValue("businessId");
        return Guid.TryParse(claim, out var id) ? id : null;
    }

    private bool IsAuthorized()
    {
        if (!Request.Headers.TryGetValue("X-Admin-Key", out var hk) || string.IsNullOrWhiteSpace(hk))
            return false;
        var key = hk.ToString().Trim();
        var globalKey = (_config["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY"))?.Trim();
        if (!string.IsNullOrWhiteSpace(globalKey) && SafeEquals(key, globalKey)) return true;
        string?[] legacy = [Environment.GetEnvironmentVariable("WHATSAPP_ADMIN_KEY"), _config["WhatsApp:AdminKey"]];
        return legacy.Any(s => !string.IsNullOrWhiteSpace(s) && SafeEquals(key, s!.Trim()));
    }

    private async Task<bool> IsAuthorizedForBusinessAsync(Guid businessId, CancellationToken ct)
    {
        if (IsAuthorized()) return true; // global admin
        if (!Request.Headers.TryGetValue("X-Admin-Key", out var hk) || string.IsNullOrWhiteSpace(hk))
            return false;
        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT "AdminKey" FROM "Businesses"
                WHERE LOWER(CAST("Id" AS TEXT)) = LOWER(@bid)
                LIMIT 1
            """;
            var p = cmd.CreateParameter(); p.ParameterName = "bid"; p.Value = businessId.ToString();
            cmd.Parameters.Add(p);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is not null and not DBNull && SafeEquals(hk.ToString().Trim(), result.ToString()?.Trim() ?? "");
        }
        catch { return false; }
    }

    private static bool SafeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    // GET /api/admin/exports/orders.csv?take=5000&businessId=xxx
    [HttpGet("orders.csv")]
    public async Task<IActionResult> ExportOrdersCsv([FromQuery] int? take = null, [FromQuery] Guid? businessId = null, CancellationToken ct = default)
    {
        try
        {
            // JWT users are scoped to their own business
            var jwtBizId = GetJwtBusinessId();
            if (jwtBizId.HasValue)
                businessId = jwtBizId.Value;

            // JWT auth for scoped business
            var role = User.FindFirstValue(ClaimTypes.Role);
            var isJwtAuth = role is "Owner" or "Manager" && jwtBizId.HasValue && businessId == jwtBizId.Value;

            if (!isJwtAuth)
            {
                if (businessId.HasValue)
                {
                    if (!await IsAuthorizedForBusinessAsync(businessId.Value, ct)) return Unauthorized();
                }
                else if (!IsAuthorized()) return Unauthorized();
            }

            var max = ClampTake(take);
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

            var bizFilter = businessId.HasValue ? """ WHERE "BusinessId" = @bid""" : "";
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT "Id", "CreatedAtUtc", "Status", "CustomerPhone", "CustomerName",
                       "PaymentMethod", "SubtotalAmount", "TotalAmount", "Address",
                       "ReceiverName", "AdditionalNotes"
                FROM "Orders"{bizFilter}
                ORDER BY "CreatedAtUtc" DESC
                LIMIT @take
            """;
            if (businessId.HasValue)
            {
                var bp = cmd.CreateParameter(); bp.ParameterName = "bid"; bp.Value = businessId.Value;
                cmd.Parameters.Add(bp);
            }
            var tp = cmd.CreateParameter(); tp.ParameterName = "take"; tp.Value = max;
            cmd.Parameters.Add(tp);

            var sb = new StringBuilder();
            sb.AppendLine("OrderId,CreatedAtUtc,Status,CustomerPhone,CustomerName,PaymentMethod,SubtotalAmount,TotalAmount,Address,ReceiverName,AdditionalNotes");

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string? Col(int i) => reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
                sb.Append(Escape(Col(0))); sb.Append(',');
                sb.Append(Escape(Col(1))); sb.Append(',');
                sb.Append(Escape(Col(2))); sb.Append(',');
                sb.Append(Escape(Col(3))); sb.Append(',');
                sb.Append(Escape(Col(4))); sb.Append(',');
                sb.Append(Escape(Col(5))); sb.Append(',');
                sb.Append(Escape(Col(6))); sb.Append(',');
                sb.Append(Escape(Col(7))); sb.Append(',');
                sb.Append(Escape(Col(8))); sb.Append(',');
                sb.Append(Escape(Col(9))); sb.Append(',');
                sb.Append(Escape(Col(10)));
                sb.Append('\n');
            }

            return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "orders.csv");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CSV orders export failed");
            return StatusCode(500, new { error = $"Export failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    // GET /api/admin/exports/customers.csv?businessId=xxx
    [HttpGet("customers.csv")]
    public async Task<IActionResult> ExportCustomersCsv([FromQuery] int? take = null, [FromQuery] Guid? businessId = null, CancellationToken ct = default)
    {
        try
        {
            // JWT users are scoped to their own business
            var jwtBizId = GetJwtBusinessId();
            if (jwtBizId.HasValue)
                businessId = jwtBizId.Value;

            var role = User.FindFirstValue(ClaimTypes.Role);
            var isJwtAuth = role is "Owner" or "Manager" && jwtBizId.HasValue && businessId == jwtBizId.Value;

            if (!isJwtAuth)
            {
                if (businessId.HasValue)
                {
                    if (!await IsAuthorizedForBusinessAsync(businessId.Value, ct)) return Unauthorized();
                }
                else if (!IsAuthorized()) return Unauthorized();
            }

            var max = ClampTake(take);
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);

            var bizFilter = businessId.HasValue ? """ AND o."BusinessId" = @bid""" : "";
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT o."CustomerPhone", MIN(o."CustomerName") AS name,
                       COUNT(*) AS orders_count, COALESCE(SUM(o."TotalAmount"), 0) AS total_spent,
                       MIN(o."CreatedAtUtc") AS first_seen, MAX(o."CreatedAtUtc") AS last_seen
                FROM "Orders" o
                WHERE o."CustomerPhone" IS NOT NULL AND o."CustomerPhone" != ''{bizFilter}
                GROUP BY o."CustomerPhone"
                ORDER BY total_spent DESC
                LIMIT @take
            """;
            if (businessId.HasValue)
            {
                var bp = cmd.CreateParameter(); bp.ParameterName = "bid"; bp.Value = businessId.Value;
                cmd.Parameters.Add(bp);
            }
            var tp = cmd.CreateParameter(); tp.ParameterName = "take"; tp.Value = max;
            cmd.Parameters.Add(tp);

            var sb = new StringBuilder();
            sb.AppendLine("Phone,Name,OrdersCount,TotalSpent,FirstSeenAtUtc,LastSeenAtUtc,LastPurchaseAtUtc");

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string? Col(int i) => reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
                sb.Append(Escape(Col(0))); sb.Append(',');
                sb.Append(Escape(Col(1))); sb.Append(',');
                sb.Append(Escape(Col(2))); sb.Append(',');
                sb.Append(Escape(Col(3))); sb.Append(',');
                sb.Append(Escape(Col(4))); sb.Append(',');
                sb.Append(Escape(Col(5))); sb.Append(',');
                sb.Append(Escape(Col(5))); // LastPurchaseAtUtc = LastSeen
                sb.Append('\n');
            }

            return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "customers.csv");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CSV customers export failed");
            return StatusCode(500, new { error = $"Export failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    private static int ClampTake(int? take)
    {
        var value = take ?? 5000;
        if (value < 1) return 1;
        if (value > 50000) return 50000;
        return value;
    }

    private static string ToIsoUtc(DateTime utc)
    {
        return utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    private static string ToDecimalString(decimal? value)
    {
        return (value ?? 0m).ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string Escape(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return "";

        if (input.Contains(',') || input.Contains('"') || input.Contains('\n'))
        {
            input = input.Replace("\"", "\"\"");
            return $"\"{input}\"";
        }

        return input;
    }
}
