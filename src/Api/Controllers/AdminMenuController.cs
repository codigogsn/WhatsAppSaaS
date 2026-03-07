using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Controllers;

[ApiController]
[Route("api/admin/menu")]
public sealed class AdminMenuController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public AdminMenuController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    private async Task<bool> IsAuthorizedForBusinessAsync(Guid businessId, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Admin-Key", out var hk) || string.IsNullOrWhiteSpace(hk))
            return false;

        // Accept global admin key
        var globalKey = _config["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY");
        if (!string.IsNullOrWhiteSpace(globalKey) && hk.ToString() == globalKey)
            return true;

        // Accept per-business admin key
        var biz = await _db.Businesses
            .AsNoTracking()
            .Where(b => b.Id == businessId && b.IsActive)
            .Select(b => new { b.AdminKey })
            .FirstOrDefaultAsync(ct);

        return biz is not null && biz.AdminKey == hk.ToString();
    }

    private bool IsAuthorized()
    {
        var adminKey = _config["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY");
        if (string.IsNullOrWhiteSpace(adminKey)) return false;
        return Request.Headers.TryGetValue("X-Admin-Key", out var hk) && hk.ToString() == adminKey;
    }

    // ── Categories ──

    // GET /api/admin/menu/categories?businessId=xxx
    [HttpGet("categories")]
    public async Task<IActionResult> ListCategories([FromQuery] Guid businessId, CancellationToken ct)
    {
        if (!await IsAuthorizedForBusinessAsync(businessId, ct)) return Unauthorized();

        var cats = await _db.MenuCategories
            .AsNoTracking()
            .Where(c => c.BusinessId == businessId)
            .OrderBy(c => c.SortOrder)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.SortOrder,
                c.IsActive,
                ItemCount = c.Items.Count
            })
            .ToListAsync(ct);

        return Ok(cats);
    }

    public sealed class CategoryRequest
    {
        public Guid BusinessId { get; set; }
        public string Name { get; set; } = "";
        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;
    }

    // POST /api/admin/menu/categories
    [HttpPost("categories")]
    public async Task<IActionResult> CreateCategory([FromBody] CategoryRequest req, CancellationToken ct)
    {
        if (!await IsAuthorizedForBusinessAsync(req.BusinessId, ct)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { error = "Name required" });

        var cat = new MenuCategory
        {
            BusinessId = req.BusinessId,
            Name = req.Name.Trim(),
            SortOrder = req.SortOrder,
            IsActive = req.IsActive
        };

        _db.MenuCategories.Add(cat);
        await _db.SaveChangesAsync(ct);

        return Ok(new { cat.Id, cat.Name, cat.SortOrder, cat.IsActive });
    }

    // PUT /api/admin/menu/categories/{id}
    [HttpPut("categories/{id:guid}")]
    public async Task<IActionResult> UpdateCategory(Guid id, [FromBody] CategoryRequest req, CancellationToken ct)
    {
        var cat = await _db.MenuCategories.FindAsync(new object[] { id }, ct);
        if (cat is null) return NotFound();

        if (!await IsAuthorizedForBusinessAsync(cat.BusinessId, ct)) return Unauthorized();

        if (!string.IsNullOrWhiteSpace(req.Name)) cat.Name = req.Name.Trim();
        cat.SortOrder = req.SortOrder;
        cat.IsActive = req.IsActive;

        await _db.SaveChangesAsync(ct);
        return Ok(new { cat.Id, cat.Name, cat.SortOrder, cat.IsActive });
    }

    // DELETE /api/admin/menu/categories/{id}
    [HttpDelete("categories/{id:guid}")]
    public async Task<IActionResult> DeleteCategory(Guid id, CancellationToken ct)
    {
        var cat = await _db.MenuCategories
            .Include(c => c.Items).ThenInclude(i => i.Aliases)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (cat is null) return NotFound();

        if (!await IsAuthorizedForBusinessAsync(cat.BusinessId, ct)) return Unauthorized();

        _db.MenuCategories.Remove(cat);
        await _db.SaveChangesAsync(ct);
        return Ok(new { deleted = true });
    }

    // ── Items ──

    // GET /api/admin/menu/items?categoryId=xxx
    [HttpGet("items")]
    public async Task<IActionResult> ListItems([FromQuery] Guid? categoryId, [FromQuery] Guid? businessId, CancellationToken ct)
    {
        if (businessId.HasValue)
        {
            if (!await IsAuthorizedForBusinessAsync(businessId.Value, ct)) return Unauthorized();
        }
        else if (!IsAuthorized()) return Unauthorized();

        var q = _db.MenuItems
            .AsNoTracking()
            .Include(i => i.Aliases)
            .Include(i => i.Category)
            .AsQueryable();

        if (categoryId.HasValue)
            q = q.Where(i => i.CategoryId == categoryId.Value);
        else if (businessId.HasValue)
            q = q.Where(i => i.Category!.BusinessId == businessId.Value);

        var items = await q
            .OrderBy(i => i.Category!.SortOrder).ThenBy(i => i.SortOrder)
            .Select(i => new
            {
                i.Id,
                i.Name,
                i.Price,
                i.Description,
                i.IsAvailable,
                i.SortOrder,
                CategoryId = i.CategoryId,
                CategoryName = i.Category!.Name,
                Aliases = i.Aliases.Select(a => new { a.Id, a.Alias }).ToList()
            })
            .ToListAsync(ct);

        return Ok(items);
    }

    public sealed class ItemRequest
    {
        public Guid CategoryId { get; set; }
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
        public string? Description { get; set; }
        public bool IsAvailable { get; set; } = true;
        public int SortOrder { get; set; }
        public List<string>? Aliases { get; set; }
    }

    // POST /api/admin/menu/items
    [HttpPost("items")]
    public async Task<IActionResult> CreateItem([FromBody] ItemRequest req, CancellationToken ct)
    {
        // Resolve businessId from category
        var catBiz = await _db.MenuCategories.AsNoTracking()
            .Where(c => c.Id == req.CategoryId)
            .Select(c => c.BusinessId)
            .FirstOrDefaultAsync(ct);
        if (catBiz == Guid.Empty) return BadRequest(new { error = "Invalid categoryId" });
        if (!await IsAuthorizedForBusinessAsync(catBiz, ct)) return Unauthorized();
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { error = "Name required" });

        var item = new MenuItem
        {
            CategoryId = req.CategoryId,
            Name = req.Name.Trim(),
            Price = req.Price,
            Description = req.Description?.Trim(),
            IsAvailable = req.IsAvailable,
            SortOrder = req.SortOrder
        };

        if (req.Aliases is { Count: > 0 })
        {
            item.Aliases = req.Aliases
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => new MenuItemAlias { Alias = a.Trim().ToLowerInvariant() })
                .ToList();
        }

        _db.MenuItems.Add(item);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            item.Id,
            item.Name,
            item.Price,
            item.Description,
            item.IsAvailable,
            item.SortOrder,
            Aliases = item.Aliases.Select(a => new { a.Id, a.Alias })
        });
    }

    // PUT /api/admin/menu/items/{id}
    [HttpPut("items/{id:guid}")]
    public async Task<IActionResult> UpdateItem(Guid id, [FromBody] ItemRequest req, CancellationToken ct)
    {
        var item = await _db.MenuItems
            .Include(i => i.Aliases)
            .Include(i => i.Category)
            .FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return NotFound();

        if (!await IsAuthorizedForBusinessAsync(item.Category!.BusinessId, ct)) return Unauthorized();

        if (!string.IsNullOrWhiteSpace(req.Name)) item.Name = req.Name.Trim();
        item.Price = req.Price;
        item.Description = req.Description?.Trim();
        item.IsAvailable = req.IsAvailable;
        item.SortOrder = req.SortOrder;

        if (req.Aliases is not null)
        {
            item.Aliases.Clear();
            item.Aliases = req.Aliases
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => new MenuItemAlias { MenuItemId = item.Id, Alias = a.Trim().ToLowerInvariant() })
                .ToList();
        }

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            item.Id,
            item.Name,
            item.Price,
            item.Description,
            item.IsAvailable,
            item.SortOrder,
            Aliases = item.Aliases.Select(a => new { a.Id, a.Alias })
        });
    }

    // DELETE /api/admin/menu/items/{id}
    [HttpDelete("items/{id:guid}")]
    public async Task<IActionResult> DeleteItem(Guid id, CancellationToken ct)
    {
        var item = await _db.MenuItems
            .Include(i => i.Aliases)
            .Include(i => i.Category)
            .FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return NotFound();

        if (!await IsAuthorizedForBusinessAsync(item.Category!.BusinessId, ct)) return Unauthorized();

        _db.MenuItems.Remove(item);
        await _db.SaveChangesAsync(ct);
        return Ok(new { deleted = true });
    }

    // PATCH /api/admin/menu/items/{id}/availability
    [HttpPatch("items/{id:guid}/availability")]
    public async Task<IActionResult> ToggleAvailability(Guid id, [FromBody] AvailabilityRequest req, CancellationToken ct)
    {
        var item = await _db.MenuItems
            .Include(i => i.Category)
            .FirstOrDefaultAsync(i => i.Id == id, ct);
        if (item is null) return NotFound();

        if (!await IsAuthorizedForBusinessAsync(item.Category!.BusinessId, ct)) return Unauthorized();

        item.IsAvailable = req.IsAvailable;
        await _db.SaveChangesAsync(ct);

        return Ok(new { item.Id, item.Name, item.IsAvailable });
    }

    public sealed class AvailabilityRequest
    {
        public bool IsAvailable { get; set; }
    }

    // ── Aliases ──

    // POST /api/admin/menu/items/{itemId}/aliases
    [HttpPost("items/{itemId:guid}/aliases")]
    public async Task<IActionResult> AddAlias(Guid itemId, [FromBody] AliasRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Alias)) return BadRequest(new { error = "Alias required" });

        var item = await _db.MenuItems
            .Include(i => i.Category)
            .FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return NotFound();

        if (!await IsAuthorizedForBusinessAsync(item.Category!.BusinessId, ct)) return Unauthorized();

        var alias = new MenuItemAlias
        {
            MenuItemId = itemId,
            Alias = req.Alias.Trim().ToLowerInvariant()
        };

        _db.MenuItemAliases.Add(alias);
        await _db.SaveChangesAsync(ct);

        return Ok(new { alias.Id, alias.Alias, alias.MenuItemId });
    }

    // DELETE /api/admin/menu/aliases/{id}
    [HttpDelete("aliases/{id:guid}")]
    public async Task<IActionResult> DeleteAlias(Guid id, CancellationToken ct)
    {
        var alias = await _db.MenuItemAliases
            .Include(a => a.MenuItem).ThenInclude(i => i!.Category)
            .FirstOrDefaultAsync(a => a.Id == id, ct);
        if (alias is null) return NotFound();

        if (!await IsAuthorizedForBusinessAsync(alias.MenuItem!.Category!.BusinessId, ct)) return Unauthorized();

        _db.MenuItemAliases.Remove(alias);
        await _db.SaveChangesAsync(ct);
        return Ok(new { deleted = true });
    }

    public sealed class AliasRequest
    {
        public string Alias { get; set; } = "";
    }

    // ── Public menu (no auth) ──

    // GET /api/menu?businessId=xxx
    [HttpGet("/api/menu")]
    public async Task<IActionResult> PublicMenu([FromQuery] Guid businessId, CancellationToken ct)
    {
        var categories = await _db.MenuCategories
            .AsNoTracking()
            .Where(c => c.BusinessId == businessId && c.IsActive)
            .OrderBy(c => c.SortOrder)
            .Select(c => new
            {
                c.Name,
                Items = c.Items
                    .Where(i => i.IsAvailable)
                    .OrderBy(i => i.SortOrder)
                    .Select(i => new
                    {
                        i.Name,
                        i.Price,
                        i.Description
                    })
            })
            .ToListAsync(ct);

        return Ok(categories);
    }
}
