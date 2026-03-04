using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace Api.Controllers;

[ApiController]
[Route("api/admin/analytics")]
public class AdminAnalyticsController : ControllerBase
{
    private readonly AppDbContext _db;

    public AdminAnalyticsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary()
    {
        // Traemos solo lo necesario (y evitamos problemas de provider/linq)
        var rows = await _db.Orders
            .AsNoTracking()
            .Select(o => new
            {
                o.Id,
                o.TotalAmount,
                o.CustomerPhone
            })
            .ToListAsync();

        var totalOrders = rows.Count;

        // Null-safe
        var totalRevenue = rows.Sum(x => x.TotalAmount ?? 0m);

        var avgTicket = totalOrders == 0
            ? 0m
            : totalRevenue / totalOrders;

        var uniqueCustomers = rows
            .Select(x => (x.CustomerPhone ?? "").Trim())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct()
            .Count();

        return Ok(new
        {
            orders = totalOrders,
            totalRevenue,
            avgTicket,
            uniqueCustomers
        });
    }
}
