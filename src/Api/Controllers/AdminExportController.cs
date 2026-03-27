using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Api.Auth;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace Api.Controllers;

[ApiController]
[Route("admin/export")]
[EnableRateLimiting("admin")]
public class AdminExportController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public AdminExportController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    private async Task<Guid?> AuthorizeBusinessAsync(Guid businessId, CancellationToken ct)
    {
        // Path 1: JWT with business scope (preferred)
        if (AdminAuth.IsJwtAuthorizedForBusiness(User, businessId))
            return businessId;

        // Path 2: Global admin key
        if (AdminAuth.IsGlobalAdminKey(Request, _config))
            return businessId;

        // Path 3: Per-business admin key (legacy)
        if (!Request.Headers.TryGetValue("X-Admin-Key", out var headerKey) || string.IsNullOrWhiteSpace(headerKey))
            return null;

        var biz = await _db.Businesses
            .AsNoTracking()
            .Where(b => b.Id == businessId && b.IsActive)
            .Select(b => new { b.Id, b.AdminKey })
            .FirstOrDefaultAsync(ct);

        if (biz is null || string.IsNullOrWhiteSpace(biz.AdminKey)
            || !AdminAuth.SafeEquals(headerKey.ToString().Trim(), biz.AdminKey.Trim()))
            return null;

        return biz.Id;
    }

    [HttpGet("orders")]
    public async Task<IActionResult> ExportOrders([FromQuery] Guid businessId, CancellationToken ct)
    {
        var bizId = await AuthorizeBusinessAsync(businessId, ct);
        if (bizId is null) return Unauthorized();

        var orders = await _db.Orders
            .AsNoTracking()
            .Where(o => o.BusinessId == bizId)
            .Include(o => o.Items)
            .OrderByDescending(o => o.CreatedAtUtc)
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("OrderId,CreatedAtUtc,Status,CustomerName,CustomerPhone,DeliveryType,PaymentMethod,SubtotalAmount,DeliveryFee,TotalAmount,Items");

        foreach (var o in orders)
        {
            var items = string.Join("; ", o.Items.Select(i => $"{i.Quantity}x {i.Name}"));
            sb.AppendLine($"{o.Id},{o.CreatedAtUtc:O},{Csv(o.Status)},{Csv(o.CustomerName)},{Csv(o.CustomerPhone)},{Csv(o.DeliveryType)},{Csv(o.PaymentMethod)},{o.SubtotalAmount},{o.DeliveryFee},{o.TotalAmount},\"{Csv(items)}\"");
        }

        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "orders.csv");
    }

    [HttpGet("customers")]
    public async Task<IActionResult> ExportCustomers([FromQuery] Guid businessId, CancellationToken ct)
    {
        var bizId = await AuthorizeBusinessAsync(businessId, ct);
        if (bizId is null) return Unauthorized();

        var customers = await _db.Customers
            .AsNoTracking()
            .Where(c => c.BusinessId == bizId)
            .OrderByDescending(c => c.TotalSpent)
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("PhoneE164,Name,TotalSpent,OrdersCount,FirstSeenAtUtc,LastSeenAtUtc,LastPurchaseAtUtc");

        foreach (var c in customers)
        {
            sb.AppendLine($"{Csv(c.PhoneE164)},{Csv(c.Name)},{c.TotalSpent},{c.OrdersCount},{c.FirstSeenAtUtc:O},{c.LastSeenAtUtc:O},{c.LastPurchaseAtUtc:O}");
        }

        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "customers.csv");
    }

    [HttpGet("products")]
    public async Task<IActionResult> ExportProducts([FromQuery] Guid businessId, CancellationToken ct)
    {
        var bizId = await AuthorizeBusinessAsync(businessId, ct);
        if (bizId is null) return Unauthorized();

        var items = await _db.OrderItems
            .AsNoTracking()
            .Where(oi => oi.Order != null && oi.Order.BusinessId == bizId)
            .Select(oi => new { oi.Name, oi.Quantity, oi.UnitPrice })
            .ToListAsync(ct);

        var grouped = items
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name!.Trim().ToLowerInvariant())
            .Select(g => new
            {
                Name = g.Key,
                TotalQuantity = g.Sum(x => x.Quantity),
                TotalRevenue = g.Sum(x => (x.UnitPrice ?? 0m) * x.Quantity)
            })
            .OrderByDescending(x => x.TotalQuantity);

        var sb = new StringBuilder();
        sb.AppendLine("ProductName,TotalQuantity,TotalRevenue");

        foreach (var p in grouped)
        {
            sb.AppendLine($"{Csv(p.Name)},{p.TotalQuantity},{p.TotalRevenue}");
        }

        return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "products.csv");
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
