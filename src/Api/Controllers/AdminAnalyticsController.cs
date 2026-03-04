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
        var orders = await _db.Orders
            .AsNoTracking()
            .ToListAsync();

        var totalOrders = orders.Count;

        var totalRevenue = orders.Sum(o => o.TotalAmount ?? 0);

        var avgTicket = totalOrders == 0
            ? 0
            : totalRevenue / totalOrders;

        var uniqueCustomers = orders
            .Select(o => o.CustomerPhone)
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
