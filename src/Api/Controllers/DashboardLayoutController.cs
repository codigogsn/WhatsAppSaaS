using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Controllers;

[ApiController]
[Route("api/dashboard/layout")]
[EnableRateLimiting("admin")]
[Authorize]
public sealed class DashboardLayoutController : ControllerBase
{
    private readonly AppDbContext _db;

    public DashboardLayoutController(AppDbContext db) => _db = db;

    private Guid? GetBusinessId() =>
        Guid.TryParse(User.FindFirstValue("businessId"), out var id) ? id : null;

    private static readonly string DefaultLayoutJson = JsonSerializer.Serialize(new
    {
        modules = new object[]
        {
            new { id = "revenue", visible = true, position = 1 },
            new { id = "performance", visible = true, position = 2 },
            new { id = "alerts", visible = true, position = 3 },
            new { id = "statusStrip", visible = true, position = 4 },
            new { id = "analytics", visible = true, position = 5 },
            new { id = "handoffs", visible = true, position = 6 },
            new { id = "customers", visible = true, position = 7 }
        }
    });

    // GET /api/dashboard/layout
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var bizId = GetBusinessId();
        if (bizId is null) return Unauthorized();

        var layout = await _db.DashboardLayouts
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.BusinessId == bizId.Value, ct);

        if (layout is null)
            return Ok(JsonSerializer.Deserialize<JsonElement>(DefaultLayoutJson));

        return Ok(JsonSerializer.Deserialize<JsonElement>(layout.LayoutJson));
    }

    // POST /api/dashboard/layout
    [HttpPost]
    public async Task<IActionResult> Save([FromBody] JsonElement body, CancellationToken ct)
    {
        var bizId = GetBusinessId();
        if (bizId is null) return Unauthorized();

        var role = User.FindFirstValue(ClaimTypes.Role);
        if (role is not ("Founder" or "Owner" or "Manager"))
            return Forbid();

        var json = body.GetRawText();

        var existing = await _db.DashboardLayouts
            .FirstOrDefaultAsync(l => l.BusinessId == bizId.Value, ct);

        if (existing is not null)
        {
            existing.LayoutJson = json;
            existing.UpdatedAtUtc = DateTime.UtcNow;
        }
        else
        {
            _db.DashboardLayouts.Add(new DashboardLayout
            {
                BusinessId = bizId.Value,
                LayoutJson = json
            });
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { saved = true });
    }
}
