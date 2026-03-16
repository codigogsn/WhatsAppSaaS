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
    // Uses raw ADO.NET to be immune to column-type mismatches (text vs uuid) in legacy schemas.
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Admin-Key", out var headerKey))
            return Unauthorized(new { error = "Missing X-Admin-Key header" });

        var key = headerKey.ToString().Trim();
        if (string.IsNullOrWhiteSpace(key))
            return Unauthorized(new { error = "Empty X-Admin-Key header" });

        var isGlobal = IsGlobalAdmin() || IsGlobalAdminLegacy(key);

        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            using var cmd = conn.CreateCommand();

            // Build query — read ALL columns as their raw DB type (text-safe)
            // SELECT * avoids "column does not exist" if legacy schema is missing columns
            if (isGlobal)
            {
                cmd.CommandText = """SELECT * FROM "Businesses" ORDER BY "CreatedAtUtc" DESC""";
            }
            else
            {
                cmd.CommandText = """SELECT * FROM "Businesses" WHERE "AdminKey" = @key ORDER BY "CreatedAtUtc" DESC""";
                var p = cmd.CreateParameter();
                p.ParameterName = "key";
                p.Value = key;
                cmd.Parameters.Add(p);
            }

            var items = new List<object>();
            using var reader = await cmd.ExecuteReaderAsync(ct);

            // Build column index lookup (some columns may not exist in legacy schemas)
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
                cols.Add(reader.GetName(i));

            string? Col(string name) =>
                cols.Contains(name) && reader[name] is not DBNull ? reader[name]?.ToString() : null;

            while (await reader.ReadAsync(ct))
            {
                var rawIsActive = cols.Contains("IsActive") ? reader["IsActive"] : (object)true;

                items.Add(new
                {
                    id = Col("Id") ?? "",
                    name = Col("Name") ?? "",
                    phoneNumberId = Col("PhoneNumberId") ?? "",
                    isActive = rawIsActive switch
                    {
                        bool b => b,
                        int i => i != 0,
                        long l => l != 0,
                        string s => s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase),
                        DBNull => true,
                        _ => true
                    },
                    greeting = Col("Greeting"),
                    schedule = Col("Schedule"),
                    address = Col("Address"),
                    logoUrl = Col("LogoUrl"),
                    paymentMobileBank = Col("PaymentMobileBank"),
                    paymentMobileId = Col("PaymentMobileId"),
                    paymentMobilePhone = Col("PaymentMobilePhone"),
                    notificationPhone = Col("NotificationPhone"),
                    restaurantType = Col("RestaurantType"),
                    menuPdfUrl = Col("MenuPdfUrl"),
                    createdAtUtc = Col("CreatedAtUtc")
                });
            }

            if (isGlobal)
                return Ok(items);

            if (items.Count == 0)
                return Unauthorized(new { error = "Invalid admin key — no matching business found" });

            return Ok(items);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"DB query failed: {ex.GetType().Name}: {ex.Message}" });
        }
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
        await _db.SaveChangesAsync(ct);

        var (categoryNames, templateName) = await SeedRestaurantTemplateAsync(biz.Id, biz.RestaurantType, ct);

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
    /// Uses raw ADO.NET because production IsActive/IsAvailable columns are
    /// integer, not boolean — EF INSERTs would fail with type mismatch.
    /// </summary>
    private async Task<(List<string> categoryNames, string? templateName)> SeedRestaurantTemplateAsync(
        Guid businessId, string? restaurantType, CancellationToken ct)
    {
        var categoryNames = new List<string>();

        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        // Idempotency: skip if business already has menu data
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = """SELECT COUNT(*) FROM "MenuCategories" WHERE "BusinessId" = @bid""";
        var bp = checkCmd.CreateParameter();
        bp.ParameterName = "bid";
        bp.Value = businessId;
        checkCmd.Parameters.Add(bp);
        var count = Convert.ToInt64(await checkCmd.ExecuteScalarAsync(ct));
        if (count > 0)
            return (categoryNames, null);

        var template = RestaurantTemplates.Get(restaurantType);

        if (template is not null)
        {
            for (var i = 0; i < template.DefaultCategories.Count; i++)
            {
                var tc = template.DefaultCategories[i];
                var catId = Guid.NewGuid();

                using var catCmd = conn.CreateCommand();
                catCmd.CommandText = """
                    INSERT INTO "MenuCategories" ("Id", "BusinessId", "Name", "SortOrder", "IsActive", "CreatedAtUtc")
                    VALUES (@id, @bid, @name, @sort, @active, @created)
                """;
                AddParam(catCmd, "id", catId);
                AddParam(catCmd, "bid", businessId);
                AddParam(catCmd, "name", tc.Name);
                AddParam(catCmd, "sort", i);
                AddParam(catCmd, "active", 1);
                AddParam(catCmd, "created", DateTime.UtcNow);
                await catCmd.ExecuteNonQueryAsync(ct);
                categoryNames.Add(tc.Name);

                for (var j = 0; j < tc.Items.Count; j++)
                {
                    var ti = tc.Items[j];
                    var itemId = Guid.NewGuid();

                    using var itemCmd = conn.CreateCommand();
                    itemCmd.CommandText = """
                        INSERT INTO "MenuItems" ("Id", "CategoryId", "Name", "Price", "Description", "IsAvailable", "SortOrder", "CreatedAtUtc")
                        VALUES (@id, @cid, @name, @price, NULL, @avail, @sort, @created)
                    """;
                    AddParam(itemCmd, "id", itemId);
                    AddParam(itemCmd, "cid", catId);
                    AddParam(itemCmd, "name", ti.Name);
                    AddParam(itemCmd, "price", ti.Price);
                    AddParam(itemCmd, "avail", 1);
                    AddParam(itemCmd, "sort", j);
                    AddParam(itemCmd, "created", DateTime.UtcNow);
                    await itemCmd.ExecuteNonQueryAsync(ct);

                    foreach (var alias in ti.Aliases)
                    {
                        using var aliasCmd = conn.CreateCommand();
                        aliasCmd.CommandText = """
                            INSERT INTO "MenuItemAliases" ("Id", "MenuItemId", "Alias")
                            VALUES (@id, @mid, @alias)
                        """;
                        AddParam(aliasCmd, "id", Guid.NewGuid());
                        AddParam(aliasCmd, "mid", itemId);
                        AddParam(aliasCmd, "alias", alias);
                        await aliasCmd.ExecuteNonQueryAsync(ct);
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
                using var catCmd = conn.CreateCommand();
                catCmd.CommandText = """
                    INSERT INTO "MenuCategories" ("Id", "BusinessId", "Name", "SortOrder", "IsActive", "CreatedAtUtc")
                    VALUES (@id, @bid, @name, @sort, @active, @created)
                """;
                AddParam(catCmd, "id", Guid.NewGuid());
                AddParam(catCmd, "bid", businessId);
                AddParam(catCmd, "name", defaultCats[i]);
                AddParam(catCmd, "sort", i);
                AddParam(catCmd, "active", 1);
                AddParam(catCmd, "created", DateTime.UtcNow);
                await catCmd.ExecuteNonQueryAsync(ct);
                categoryNames.Add(defaultCats[i]);
            }
        }

        return (categoryNames, template?.Name);
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
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

    // POST /api/admin/businesses/cleanup — deactivate junk/test businesses
    // Safe: only deactivates, does not delete. Preserves FK integrity.
    [HttpPost("cleanup")]
    public async Task<IActionResult> Cleanup(CancellationToken ct)
    {
        if (!IsGlobalAdmin())
            return Unauthorized();

        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            // Deactivate businesses with obvious junk/test data:
            // - Name is "Default Business" (auto-created by BusinessResolver with placeholder phone IDs)
            // - PhoneNumberId contains non-numeric characters (like "erify2", "ER_ID>", "verify")
            //   indicating test/placeholder values
            // - But preserve the Demo Restaurant (11111111-...) if it has a real phone number
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE "Businesses"
                SET "IsActive" = 0
                WHERE "IsActive"::boolean = true
                  AND (
                    ("Name" = 'Default Business')
                    OR ("PhoneNumberId" ~ '[^0-9]')
                  )
                RETURNING "Id"::text, "Name", "PhoneNumberId"
            """;

            var deactivated = new List<object>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                deactivated.Add(new
                {
                    id = reader.GetString(0),
                    name = reader.GetString(1),
                    phoneNumberId = reader.GetString(2)
                });
            }

            return Ok(new
            {
                message = $"Deactivated {deactivated.Count} junk/test business(es)",
                deactivated
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Cleanup failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a),
            Encoding.UTF8.GetBytes(b));
    }
}
