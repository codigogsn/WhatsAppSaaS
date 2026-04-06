using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Api.Auth;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Controllers;

[ApiController]
[Route("api/admin/extras")]
[EnableRateLimiting("admin")]
public sealed class AdminExtrasController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<AdminExtrasController> _logger;

    public AdminExtrasController(AppDbContext db, IConfiguration config, ILogger<AdminExtrasController> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    // ── Auth (same pattern as AdminMenuController) ──

    private async Task<bool> IsAuthorizedForBusinessAsync(Guid businessId, CancellationToken ct)
    {
        if (AdminAuth.IsJwtAuthorizedForBusiness(User, businessId))
            return true;
        if (AdminAuth.IsGlobalAdminKey(Request, _config))
            return true;
        if (!Request.Headers.TryGetValue("X-Admin-Key", out var hk) || string.IsNullOrWhiteSpace(hk))
            return false;
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
            p.Value = businessId.ToString();
            cmd.Parameters.Add(p);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is null or DBNull) return false;
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(hk.ToString().Trim()),
                Encoding.UTF8.GetBytes(result.ToString()?.Trim() ?? ""));
        }
        catch { return false; }
    }

    // ── DTOs ──

    public sealed class CreateExtraRequest
    {
        public string Name { get; set; } = "";
        public decimal? AdditivePrice { get; set; }
        public int SortOrder { get; set; }
        public List<Guid>? MenuItemIds { get; set; }
        public List<Guid>? MenuCategoryIds { get; set; }
    }

    public sealed class UpdateExtraRequest
    {
        public string Name { get; set; } = "";
        public decimal? AdditivePrice { get; set; }
        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public List<Guid>? MenuItemIds { get; set; }
        public List<Guid>? MenuCategoryIds { get; set; }
    }

    // ── Endpoints ──

    // GET /api/admin/extras?businessId=xxx
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid businessId, CancellationToken ct)
    {
        if (!await IsAuthorizedForBusinessAsync(businessId, ct)) return Unauthorized();

        var extras = await _db.Extras
            .Where(e => e.BusinessId == businessId)
            .OrderBy(e => e.SortOrder).ThenBy(e => e.Name)
            .Select(e => new
            {
                id = e.Id,
                name = e.Name,
                additivePrice = e.AdditivePrice,
                isActive = e.IsActive,
                sortOrder = e.SortOrder,
                menuItemIds = e.MenuItems.Select(m => m.MenuItemId).ToList(),
                menuCategoryIds = e.MenuCategories.Select(c => c.MenuCategoryId).ToList()
            })
            .ToListAsync(ct);

        return Ok(extras);
    }

    // GET /api/admin/extras/{id}?businessId=xxx
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, [FromQuery] Guid businessId, CancellationToken ct)
    {
        if (!await IsAuthorizedForBusinessAsync(businessId, ct)) return Unauthorized();

        var extra = await _db.Extras
            .Where(e => e.Id == id && e.BusinessId == businessId)
            .Select(e => new
            {
                id = e.Id,
                name = e.Name,
                additivePrice = e.AdditivePrice,
                isActive = e.IsActive,
                sortOrder = e.SortOrder,
                menuItemIds = e.MenuItems.Select(m => m.MenuItemId).ToList(),
                menuCategoryIds = e.MenuCategories.Select(c => c.MenuCategoryId).ToList()
            })
            .FirstOrDefaultAsync(ct);

        return extra is null ? NotFound() : Ok(extra);
    }

    // POST /api/admin/extras?businessId=xxx
    [HttpPost]
    public async Task<IActionResult> Create([FromQuery] Guid businessId, [FromBody] CreateExtraRequest req, CancellationToken ct)
    {
        if (!await IsAuthorizedForBusinessAsync(businessId, ct)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Name is required.");

        var extra = new Extra
        {
            BusinessId = businessId,
            Name = req.Name.Trim(),
            AdditivePrice = req.AdditivePrice,
            SortOrder = req.SortOrder,
            IsActive = true
        };

        if (req.MenuItemIds is { Count: > 0 })
            extra.MenuItems = req.MenuItemIds.Distinct().Select(mid => new ExtraMenuItem { MenuItemId = mid }).ToList();

        if (req.MenuCategoryIds is { Count: > 0 })
            extra.MenuCategories = req.MenuCategoryIds.Distinct().Select(cid => new ExtraMenuCategory { MenuCategoryId = cid }).ToList();

        _db.Extras.Add(extra);
        await _db.SaveChangesAsync(ct);

        return Ok(new { id = extra.Id });
    }

    // PUT /api/admin/extras/{id}?businessId=xxx
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromQuery] Guid businessId, [FromBody] UpdateExtraRequest req, CancellationToken ct)
    {
        if (!await IsAuthorizedForBusinessAsync(businessId, ct)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Name is required.");

        var extra = await _db.Extras
            .Include(e => e.MenuItems)
            .Include(e => e.MenuCategories)
            .FirstOrDefaultAsync(e => e.Id == id && e.BusinessId == businessId, ct);

        if (extra is null) return NotFound();

        extra.Name = req.Name.Trim();
        extra.AdditivePrice = req.AdditivePrice;
        extra.SortOrder = req.SortOrder;
        extra.IsActive = req.IsActive;

        // Replace item links
        _db.ExtraMenuItems.RemoveRange(extra.MenuItems);
        extra.MenuItems = req.MenuItemIds is { Count: > 0 }
            ? req.MenuItemIds.Distinct().Select(mid => new ExtraMenuItem { ExtraId = id, MenuItemId = mid }).ToList()
            : new();

        // Replace category links
        _db.ExtraMenuCategories.RemoveRange(extra.MenuCategories);
        extra.MenuCategories = req.MenuCategoryIds is { Count: > 0 }
            ? req.MenuCategoryIds.Distinct().Select(cid => new ExtraMenuCategory { ExtraId = id, MenuCategoryId = cid }).ToList()
            : new();

        await _db.SaveChangesAsync(ct);
        return Ok(new { id = extra.Id });
    }

    // PATCH /api/admin/extras/{id}/toggle?businessId=xxx
    [HttpPatch("{id:guid}/toggle")]
    public async Task<IActionResult> Toggle(Guid id, [FromQuery] Guid businessId, CancellationToken ct)
    {
        if (!await IsAuthorizedForBusinessAsync(businessId, ct)) return Unauthorized();

        var extra = await _db.Extras
            .FirstOrDefaultAsync(e => e.Id == id && e.BusinessId == businessId, ct);

        if (extra is null) return NotFound();

        extra.IsActive = !extra.IsActive;
        await _db.SaveChangesAsync(ct);

        return Ok(new { id = extra.Id, isActive = extra.IsActive });
    }

    // DELETE /api/admin/extras/{id}?businessId=xxx
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] Guid businessId, CancellationToken ct)
    {
        if (!await IsAuthorizedForBusinessAsync(businessId, ct)) return Unauthorized();

        var extra = await _db.Extras
            .Include(e => e.MenuItems)
            .Include(e => e.MenuCategories)
            .FirstOrDefaultAsync(e => e.Id == id && e.BusinessId == businessId, ct);

        if (extra is null) return NotFound();

        _db.Extras.Remove(extra); // cascade deletes links
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }
}
