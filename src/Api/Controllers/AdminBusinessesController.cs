using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
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
        var adminKey = _config["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY");
        if (string.IsNullOrWhiteSpace(adminKey))
            return false;

        return Request.Headers.TryGetValue("X-Admin-Key", out var headerKey)
               && ConstantTimeEquals(headerKey.ToString(), adminKey);
    }

    private async Task<Business?> AuthorizeBusinessAsync(Guid businessId, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Admin-Key", out var headerKey) || string.IsNullOrWhiteSpace(headerKey))
            return null;

        var biz = await _db.Businesses.FirstOrDefaultAsync(b => b.Id == businessId && b.IsActive, ct);
        if (biz is null) return null;

        // Accept global admin key OR per-business admin key
        var globalKey = _config["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY");
        var key = headerKey.ToString();
        if ((!string.IsNullOrWhiteSpace(globalKey) && ConstantTimeEquals(key, globalKey))
            || (!string.IsNullOrWhiteSpace(biz.AdminKey) && ConstantTimeEquals(key, biz.AdminKey)))
            return biz;

        return null;
    }

    // GET /api/admin/businesses
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!IsGlobalAdmin())
            return Unauthorized();

        var items = await _db.Businesses
            .AsNoTracking()
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
                b.CreatedAtUtc
            })
            .ToListAsync(ct);

        return Ok(items);
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
            IsActive = true
        };

        _db.Businesses.Add(biz);

        // Seed default menu categories
        var defaultCategories = new[] { "Combos", "Bebidas", "Extras" };
        for (var i = 0; i < defaultCategories.Length; i++)
        {
            _db.MenuCategories.Add(new MenuCategory
            {
                BusinessId = biz.Id,
                Name = defaultCategories[i],
                SortOrder = i,
                IsActive = true
            });
        }

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
            biz.CreatedAtUtc,
            DefaultCategories = defaultCategories
        });
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
            biz.PaymentMobilePhone
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
