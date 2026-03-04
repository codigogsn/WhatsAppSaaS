using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace Api.Controllers;

[ApiController]
[Route("api/admin/exports")]
public class AdminExportsController : ControllerBase
{
    private readonly AppDbContext _db;

    public AdminExportsController(AppDbContext db)
    {
        _db = db;
    }

    // GET /api/admin/exports/orders.csv?take=5000
    [HttpGet("orders.csv")]
    public async Task<IActionResult> ExportOrdersCsv([FromQuery] int? take = null)
    {
        var max = ClampTake(take);

        var orders = await _db.Orders
            .AsNoTracking()
            .OrderByDescending(o => o.CreatedAtUtc)
            .Take(max)
            .ToListAsync();

        var sb = new StringBuilder();

        sb.AppendLine("OrderId,CreatedAtUtc,Status,CustomerPhone,CustomerName,PaymentMethod,SubtotalAmount,TotalAmount,Address,ReceiverName,AdditionalNotes");

        foreach (var o in orders)
        {
            sb.Append(Escape(o.Id.ToString())); sb.Append(',');
            sb.Append(Escape(ToIsoUtc(o.CreatedAtUtc))); sb.Append(',');
            sb.Append(Escape(o.Status.ToString())); sb.Append(',');
            sb.Append(Escape(o.CustomerPhone)); sb.Append(',');
            sb.Append(Escape(o.CustomerName)); sb.Append(',');
            sb.Append(Escape(o.PaymentMethod)); sb.Append(',');
            sb.Append(Escape(ToDecimalString(o.SubtotalAmount))); sb.Append(',');
            sb.Append(Escape(ToDecimalString(o.TotalAmount))); sb.Append(',');
            sb.Append(Escape(o.Address)); sb.Append(',');
            sb.Append(Escape(o.ReceiverName)); sb.Append(',');
            sb.Append(Escape(o.AdditionalNotes));
            sb.Append('\n');
        }

        return File(
            Encoding.UTF8.GetBytes(sb.ToString()),
            "text/csv",
            "orders.csv"
        );
    }

    // GET /api/admin/exports/customers.csv
    [HttpGet("customers.csv")]
    public async Task<IActionResult> ExportCustomersCsv([FromQuery] int? take = null)
    {
        var max = ClampTake(take);

        var orders = await _db.Orders
            .AsNoTracking()
            .Where(o => o.CustomerPhone != null && o.CustomerPhone != "")
            .ToListAsync();

        var customers = orders
            .GroupBy(o => (o.CustomerPhone ?? "").Trim())
            .Select(g =>
            {
                var phone = g.Key;

                var name = g
                    .Select(x => (x.CustomerName ?? "").Trim())
                    .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "";

                var ordersCount = g.Count();
                var totalSpent = g.Sum(x => x.TotalAmount ?? 0m);
                var firstSeen = g.Min(x => x.CreatedAtUtc);
                var lastSeen = g.Max(x => x.CreatedAtUtc);

                return new
                {
                    Phone = phone,
                    Name = name,
                    OrdersCount = ordersCount,
                    TotalSpent = totalSpent,
                    FirstSeen = firstSeen,
                    LastSeen = lastSeen
                };
            })
            .OrderByDescending(x => x.TotalSpent)
            .Take(max)
            .ToList();

        var sb = new StringBuilder();

        sb.AppendLine("Phone,Name,OrdersCount,TotalSpent,FirstSeenAtUtc,LastSeenAtUtc,LastPurchaseAtUtc");

        foreach (var c in customers)
        {
            sb.Append(Escape(c.Phone)); sb.Append(',');
            sb.Append(Escape(c.Name)); sb.Append(',');
            sb.Append(c.OrdersCount); sb.Append(',');
            sb.Append(ToDecimalString(c.TotalSpent)); sb.Append(',');
            sb.Append(ToIsoUtc(c.FirstSeen)); sb.Append(',');
            sb.Append(ToIsoUtc(c.LastSeen)); sb.Append(',');
            sb.Append(ToIsoUtc(c.LastSeen));
            sb.Append('\n');
        }

        return File(
            Encoding.UTF8.GetBytes(sb.ToString()),
            "text/csv",
            "customers.csv"
        );
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
