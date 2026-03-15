using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Application.Services;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace Api.Controllers;

[ApiController]
[Route("api/admin/businesses")]
[EnableRateLimiting("admin")]
public class AdminBusinessesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public AdminBusinessesController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    private bool IsGlobalAdmin()
    {
        var adminKey = (_config["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY"))?.Trim();
        if (string.IsNullOrWhiteSpace(adminKey))
            return false;

        return Request.Headers.TryGetValue("X-Admin-Key", out var headerKey)
               && ConstantTimeEquals(headerKey.ToString().Trim(), adminKey);
    }

    private async Task<Business?> AuthorizeBusinessAsync(Guid businessId, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Admin-Key", out var headerKey) || string.IsNullOrWhiteSpace(headerKey))
            return null;

        var biz = await _db.Businesses.FirstOrDefaultAsync(b => b.Id == businessId && b.IsActive, ct);
        if (biz is null) return null;

        // Accept global admin key OR per-business admin key
        var globalKey = (_config["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY"))?.Trim();
        var key = headerKey.ToString().Trim();
        if ((!string.IsNullOrWhiteSpace(globalKey) && ConstantTimeEquals(key, globalKey))
            || (!string.IsNullOrWhiteSpace(biz.AdminKey) && ConstantTimeEquals(key, biz.AdminKey.Trim())))
            return biz;

        return null;
    }

    // GET /api/admin/businesses
    // Global admin key → all businesses. Per-business key → only matching business.
    // Also resolves WHATSAPP_ADMIN_KEY as global for backwards compat with auto-created businesses.
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Admin-Key", out var headerKey))
            return Unauthorized(new { error = "Missing X-Admin-Key header" });

        var key = headerKey.ToString().Trim();
        if (string.IsNullOrWhiteSpace(key))
            return Unauthorized(new { error = "Empty X-Admin-Key header" });

        // Check all possible global key sources (same as BusinessResolver.ResolveOrCreateAsync)
        var isGlobal = IsGlobalAdmin() || IsGlobalAdminLegacy(key);

        IQueryable<Business> query = _db.Businesses.AsNoTracking();

        if (!isGlobal)
        {
            // Per-business key: filter to matching business only
            query = query.Where(b => b.AdminKey == key);
        }

        var items = await query
            .OrderByDescending(b => b.CreatedAtUtc)
            .Select(b => new
            {
                b.Id,
                b.Name,
                b.PhoneNumberId,
                b.IsActive,
                b.Greeting,
                b.Schedule,
                b.Address,
                b.LogoUrl,
                b.PaymentMobileBank,
                b.PaymentMobileId,
                b.PaymentMobilePhone,
                b.NotificationPhone,
                b.RestaurantType,
                b.MenuPdfUrl,
                b.CreatedAtUtc
            })
            .ToListAsync(ct);

        // Global admin with valid key: return list even if empty (no businesses yet)
        if (isGlobal)
            return Ok(items);

        // Per-business key: if no match, key is invalid
        if (items.Count == 0)
            return Unauthorized(new { error = "Invalid admin key — no matching business found" });

        return Ok(items);
    }

    /// <summary>
    /// Check legacy global key sources (WHATSAPP_ADMIN_KEY, WhatsApp__AdminKey)
    /// that BusinessResolver uses to auto-create businesses.
    /// </summary>
    private bool IsGlobalAdminLegacy(string key)
    {
        string?[] sources = [
            Environment.GetEnvironmentVariable("WHATSAPP_ADMIN_KEY"),
            _config["WhatsApp:AdminKey"],
        ];
        foreach (var src in sources)
        {
            if (!string.IsNullOrWhiteSpace(src) && ConstantTimeEquals(key.Trim(), src.Trim()))
                return true;
        }
        return false;
    }

    // GET /api/admin/businesses/{id}
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var biz = await AuthorizeBusinessAsync(id, ct);
        if (biz is null) return Unauthorized();

        return Ok(new
        {
            biz.Id,
            biz.Name,
            biz.PhoneNumberId,
            biz.IsActive,
            biz.Greeting,
            biz.Schedule,
            biz.Address,
            biz.LogoUrl,
            biz.PaymentMobileBank,
            biz.PaymentMobileId,
            biz.PaymentMobilePhone,
            biz.NotificationPhone,
            biz.RestaurantType,
            biz.MenuPdfUrl,
            biz.CreatedAtUtc
        });
    }

    public sealed class CreateBusinessRequest
    {
        public string Name { get; set; } = "";
        public string PhoneNumberId { get; set; } = "";
        public string? AccessToken { get; set; }
        public string? Greeting { get; set; }
        public string? Schedule { get; set; }
        public string? Address { get; set; }
        public string? LogoUrl { get; set; }
        public string? PaymentMobileBank { get; set; }
        public string? PaymentMobileId { get; set; }
        public string? PaymentMobilePhone { get; set; }
        public string? NotificationPhone { get; set; }
        public string? RestaurantType { get; set; }
    }

    // GET /api/admin/businesses/templates
    [HttpGet("templates")]
    public IActionResult ListTemplates()
    {
        return Ok(RestaurantTemplates.ListSummaries());
    }

    // GET /api/admin/businesses/templates/{type}
    [HttpGet("templates/{type}")]
    public IActionResult GetTemplatePreview(string type)
    {
        var preview = RestaurantTemplates.GetDetailedPreview(type);
        if (preview is null)
            return NotFound(new { error = $"Template '{type}' not found" });
        return Ok(preview);
    }

    // POST /api/admin/businesses
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBusinessRequest req, CancellationToken ct)
    {
        if (!IsGlobalAdmin())
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Name is required" });
        if (string.IsNullOrWhiteSpace(req.PhoneNumberId))
            return BadRequest(new { error = "PhoneNumberId is required" });

        // Check uniqueness
        var exists = await _db.Businesses.AnyAsync(b => b.PhoneNumberId == req.PhoneNumberId.Trim(), ct);
        if (exists)
            return Conflict(new { error = "A business with that PhoneNumberId already exists" });

        // Generate per-business admin key
        var bizAdminKey = Guid.NewGuid().ToString("N")[..24];

        var biz = new Business
        {
            Name = req.Name.Trim(),
            PhoneNumberId = req.PhoneNumberId.Trim(),
            AccessToken = req.AccessToken?.Trim() ?? "",
            AdminKey = bizAdminKey,
            Greeting = req.Greeting?.Trim(),
            Schedule = req.Schedule?.Trim(),
            Address = req.Address?.Trim(),
            LogoUrl = req.LogoUrl?.Trim(),
            PaymentMobileBank = req.PaymentMobileBank?.Trim(),
            PaymentMobileId = req.PaymentMobileId?.Trim(),
            PaymentMobilePhone = req.PaymentMobilePhone?.Trim(),
            NotificationPhone = req.NotificationPhone?.Trim(),
            RestaurantType = req.RestaurantType?.Trim().ToLowerInvariant(),
            IsActive = true
        };

        _db.Businesses.Add(biz);

        var (categoryNames, templateName) = await SeedRestaurantTemplateAsync(biz.Id, biz.RestaurantType, ct);

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            biz.Id,
            biz.Name,
            biz.PhoneNumberId,
            biz.AdminKey,
            biz.IsActive,
            biz.Greeting,
            biz.Schedule,
            biz.Address,
            biz.RestaurantType,
            biz.CreatedAtUtc,
            DefaultCategories = categoryNames,
            TemplateName = templateName,
            MenuSeeded = categoryNames.Count > 0
        });
    }

    // POST /api/admin/businesses/{id}/seed-menu
    [HttpPost("{id:guid}/seed-menu")]
    public async Task<IActionResult> SeedMenu(Guid id, CancellationToken ct)
    {
        var biz = await AuthorizeBusinessAsync(id, ct);
        if (biz is null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(biz.RestaurantType))
            return BadRequest(new { error = "Business has no RestaurantType set" });

        var (categoryNames, templateName) = await SeedRestaurantTemplateAsync(biz.Id, biz.RestaurantType, ct);

        if (categoryNames.Count == 0)
            return Ok(new { message = "Menu already exists, skipped seeding", seeded = false });

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            message = "Menu seeded from template",
            seeded = true,
            templateName,
            categories = categoryNames
        });
    }

    /// <summary>
    /// Seeds menu categories, items, and aliases from a restaurant template.
    /// Idempotent: skips if the business already has any menu categories.
    /// </summary>
    private async Task<(List<string> categoryNames, string? templateName)> SeedRestaurantTemplateAsync(
        Guid businessId, string? restaurantType, CancellationToken ct)
    {
        var categoryNames = new List<string>();

        // Idempotency: skip if business already has menu data
        var hasExistingMenu = await _db.MenuCategories.AnyAsync(c => c.BusinessId == businessId, ct);
        if (hasExistingMenu)
            return (categoryNames, null);

        var template = RestaurantTemplates.Get(restaurantType);

        if (template is not null)
        {
            for (var i = 0; i < template.DefaultCategories.Count; i++)
            {
                var tc = template.DefaultCategories[i];
                var cat = new MenuCategory
                {
                    BusinessId = businessId,
                    Name = tc.Name,
                    SortOrder = i,
                    IsActive = true
                };
                _db.MenuCategories.Add(cat);
                categoryNames.Add(tc.Name);

                for (var j = 0; j < tc.Items.Count; j++)
                {
                    var ti = tc.Items[j];
                    var item = new MenuItem
                    {
                        CategoryId = cat.Id,
                        Name = ti.Name,
                        Price = ti.Price,
                        IsAvailable = true,
                        SortOrder = j
                    };
                    _db.MenuItems.Add(item);

                    foreach (var alias in ti.Aliases)
                    {
                        _db.MenuItemAliases.Add(new MenuItemAlias
                        {
                            MenuItemId = item.Id,
                            Alias = alias
                        });
                    }
                }
            }
        }
        else
        {
            // No template: seed generic starter categories
            var defaultCats = new[] { "Combos", "Bebidas" };
            for (var i = 0; i < defaultCats.Length; i++)
            {
                _db.MenuCategories.Add(new MenuCategory
                {
                    BusinessId = businessId,
                    Name = defaultCats[i],
                    SortOrder = i,
                    IsActive = true
                });
                categoryNames.Add(defaultCats[i]);
            }
        }

        return (categoryNames, template?.Name);
    }

    public sealed class UpdateBusinessRequest
    {
        public string? Name { get; set; }
        public string? Greeting { get; set; }
        public string? Schedule { get; set; }
        public string? Address { get; set; }
        public string? LogoUrl { get; set; }
        public string? PaymentMobileBank { get; set; }
        public string? PaymentMobileId { get; set; }
        public string? PaymentMobilePhone { get; set; }
        public string? NotificationPhone { get; set; }
    }

    // PUT /api/admin/businesses/{id}
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateBusinessRequest req, CancellationToken ct)
    {
        var biz = await AuthorizeBusinessAsync(id, ct);
        if (biz is null) return Unauthorized();

        if (!string.IsNullOrWhiteSpace(req.Name)) biz.Name = req.Name.Trim();
        biz.Greeting = req.Greeting?.Trim();
        biz.Schedule = req.Schedule?.Trim();
        biz.Address = req.Address?.Trim();
        biz.LogoUrl = req.LogoUrl?.Trim();
        biz.PaymentMobileBank = req.PaymentMobileBank?.Trim();
        biz.PaymentMobileId = req.PaymentMobileId?.Trim();
        biz.PaymentMobilePhone = req.PaymentMobilePhone?.Trim();
        biz.NotificationPhone = req.NotificationPhone?.Trim();

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            biz.Id,
            biz.Name,
            biz.Greeting,
            biz.Schedule,
            biz.Address,
            biz.LogoUrl,
            biz.PaymentMobileBank,
            biz.PaymentMobileId,
            biz.PaymentMobilePhone,
            biz.NotificationPhone
        });
    }

    // PATCH /api/admin/businesses/{id}/toggle — activate/deactivate
    [HttpPatch("{id:guid}/toggle")]
    public async Task<IActionResult> Toggle(Guid id, CancellationToken ct)
    {
        if (!IsGlobalAdmin())
            return Unauthorized();

        var biz = await _db.Businesses.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (biz is null) return NotFound(new { error = "Business not found" });

        biz.IsActive = !biz.IsActive;
        await _db.SaveChangesAsync(ct);

        return Ok(new { biz.Id, biz.Name, biz.IsActive });
    }

    // POST /api/admin/businesses/seed
    [HttpPost("seed")]
    public async Task<IActionResult> Seed(CancellationToken ct)
    {
        var adminKey = _config["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY");
        if (string.IsNullOrWhiteSpace(adminKey))
            return StatusCode(500, "ADMIN_KEY missing.");

        if (!Request.Headers.TryGetValue("X-Admin-Key", out var headerKey) || !ConstantTimeEquals(headerKey.ToString(), adminKey))
            return Unauthorized();

        var phoneNumberId =
            _config["WhatsApp:PhoneNumberId"] ??
            _config["WhatsApp__PhoneNumberId"] ??
            Environment.GetEnvironmentVariable("WhatsApp__PhoneNumberId");

        var accessToken =
            _config["WhatsApp:AccessToken"] ??
            _config["WhatsApp__AccessToken"] ??
            Environment.GetEnvironmentVariable("WhatsApp__AccessToken");

        if (string.IsNullOrWhiteSpace(phoneNumberId))
            return StatusCode(500, "WhatsApp__PhoneNumberId missing.");

        if (string.IsNullOrWhiteSpace(accessToken))
            return StatusCode(500, "WhatsApp__AccessToken missing.");

        var businessId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var business = await _db.Businesses
            .FirstOrDefaultAsync(x => x.Id == businessId, ct);

        if (business == null)
        {
            business = new Business
            {
                Id = businessId,
                CreatedAtUtc = DateTime.UtcNow
            };

            _db.Businesses.Add(business);
        }

        business.Name = "Demo Restaurant";
        business.PhoneNumberId = phoneNumberId;
        business.AccessToken = accessToken;
        business.AdminKey = adminKey;
        business.IsActive = true;

        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            business.Id,
            business.Name,
            business.PhoneNumberId,
            business.IsActive
        });
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b));
    }
}
