using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Api.Auth;
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

    /// <summary>
    /// JWT-first auth, then raw ADO.NET X-Admin-Key fallback — production
    /// Businesses.IsActive is integer, not boolean.
    /// </summary>
    private async Task<Business?> AuthorizeBusinessAsync(Guid businessId, CancellationToken ct)
    {
        // Path 1: JWT with business scope
        var isJwt = AdminAuth.IsJwtAuthorizedForBusiness(User, businessId);

        // Path 2: X-Admin-Key
        var isGlobal = false;
        string? key = null;
        if (!isJwt)
        {
            if (!Request.Headers.TryGetValue("X-Admin-Key", out var headerKey) || string.IsNullOrWhiteSpace(headerKey))
                return null;

            key = headerKey.ToString().Trim();

            var globalKey = (_config["ADMIN_KEY"] ?? Environment.GetEnvironmentVariable("ADMIN_KEY"))?.Trim();
            isGlobal = !string.IsNullOrWhiteSpace(globalKey) && ConstantTimeEquals(key, globalKey);
            if (!isGlobal && IsGlobalAdminLegacy(key)) isGlobal = true;
        }

        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            using var cmd = conn.CreateCommand();
            // Use LOWER(CAST()) for cross-DB compat (PostgreSQL UUID vs SQLite TEXT)
            // Check IsActive in application code to handle int/bool/string variants
            cmd.CommandText = """
                SELECT * FROM "Businesses"
                WHERE LOWER(CAST("Id" AS TEXT)) = LOWER(@bid)
                LIMIT 1
            """;
            var p = cmd.CreateParameter();
            p.ParameterName = "bid";
            p.Value = businessId.ToString();
            cmd.Parameters.Add(p);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;

            // Check IsActive in application code (handles int/bool/string)
            var cols0 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var j = 0; j < reader.FieldCount; j++) cols0.Add(reader.GetName(j));
            if (cols0.Contains("IsActive"))
            {
                var rawActive = reader["IsActive"];
                var active = rawActive switch
                {
                    bool bv => bv, int iv => iv != 0, long lv => lv != 0,
                    string sv => sv == "1" || sv.Equals("true", StringComparison.OrdinalIgnoreCase),
                    DBNull => true, _ => true
                };
                if (!active) return null;
            }

            // Build column lookup
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) cols.Add(reader.GetName(i));
            string? Col(string name) => cols.Contains(name) && reader[name] is not DBNull ? reader[name]?.ToString() : null;

            var biz = new Business
            {
                Id = Guid.TryParse(Col("Id"), out var gid) ? gid : businessId,
                Name = Col("Name") ?? "",
                PhoneNumberId = Col("PhoneNumberId") ?? "",
                AccessToken = Col("AccessToken") ?? "",
                AdminKey = Col("AdminKey") ?? "",
                Greeting = Col("Greeting"),
                Schedule = Col("Schedule"),
                Address = Col("Address"),
                LogoUrl = Col("LogoUrl"),
                PaymentMobileBank = Col("PaymentMobileBank"),
                PaymentMobileId = Col("PaymentMobileId"),
                PaymentMobilePhone = Col("PaymentMobilePhone"),
                NotificationPhone = Col("NotificationPhone"),
                RestaurantType = Col("RestaurantType"),
                MenuPdfUrl = Col("MenuPdfUrl"),
                ZelleRecipient = Col("ZelleRecipient"),
                ZelleInstructions = Col("ZelleInstructions"),
                IsActive = true // we filtered for active
            };
            if (DateTime.TryParse(Col("CreatedAtUtc"), out var created))
                biz.CreatedAtUtc = created;

            // Check key
            if (isJwt || isGlobal) return biz;
            if (key is not null && !string.IsNullOrWhiteSpace(biz.AdminKey) && ConstantTimeEquals(key, biz.AdminKey.Trim()))
                return biz;

            return null;
        }
        catch
        {
            return null;
        }
    }

    // GET /api/admin/businesses
    // JWT Owner → own business. Global admin key → all businesses. Per-business key → matching business.
    // Uses raw ADO.NET to be immune to column-type mismatches (text vs uuid) in legacy schemas.
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        // Path 1: JWT auth — Owner/Manager sees their assigned businesses
        var jwtBizIds = AdminAuth.GetBusinessIds(User);
        var isJwtAuth = AdminAuth.HasJwtAdminAccess(User) && jwtBizIds.Count > 0;

        // Path 2: X-Admin-Key fallback
        string? key = null;
        var isGlobal = false;
        if (!isJwtAuth)
        {
            if (!Request.Headers.TryGetValue("X-Admin-Key", out var headerKey))
                return Unauthorized(new { error = "Missing authorization" });

            key = headerKey.ToString().Trim();
            if (string.IsNullOrWhiteSpace(key))
                return Unauthorized(new { error = "Empty authorization" });

            isGlobal = IsGlobalAdmin() || IsGlobalAdminLegacy(key);
        }

        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            using var cmd = conn.CreateCommand();

            // Build query — read ALL columns as their raw DB type (text-safe)
            // SELECT * avoids "column does not exist" if legacy schema is missing columns
            if (isJwtAuth)
            {
                // JWT users see all their assigned businesses
                var placeholders = new List<string>();
                for (var idx = 0; idx < jwtBizIds.Count; idx++)
                {
                    var pName = $"@bid{idx}";
                    placeholders.Add(pName);
                    var p = cmd.CreateParameter();
                    p.ParameterName = $"bid{idx}";
                    p.Value = jwtBizIds[idx].ToString();
                    cmd.Parameters.Add(p);
                }
                cmd.CommandText = $"""SELECT * FROM "Businesses" WHERE LOWER(CAST("Id" AS TEXT)) IN ({string.Join(",", placeholders.Select(ph => $"LOWER({ph})"))}) ORDER BY "CreatedAtUtc" DESC""";
            }
            else if (isGlobal)
            {
                cmd.CommandText = """SELECT * FROM "Businesses" ORDER BY "CreatedAtUtc" DESC""";
            }
            else
            {
                cmd.CommandText = """SELECT * FROM "Businesses" WHERE "AdminKey" = @key ORDER BY "CreatedAtUtc" DESC""";
                var p = cmd.CreateParameter();
                p.ParameterName = "key";
                p.Value = key!;
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
                var name = Col("Name") ?? "";
                var phoneNumberId = Col("PhoneNumberId") ?? "";
                var token = Col("AccessToken");
                var isActive = rawIsActive switch
                {
                    bool b => b,
                    int i => i != 0,
                    long l => l != 0,
                    string s => s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase),
                    DBNull => true,
                    _ => true
                };

                var (isJunk, _) = ClassifyBusiness(name, phoneNumberId, token);

                items.Add(new
                {
                    id = Col("Id") ?? "",
                    name,
                    phoneNumberId,
                    isActive,
                    isJunk,
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
                    zelleRecipient = Col("ZelleRecipient"),
                    zelleInstructions = Col("ZelleInstructions"),
                    createdAtUtc = Col("CreatedAtUtc")
                });
            }

            if (isJwtAuth || isGlobal)
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
            biz.ZelleRecipient,
            biz.ZelleInstructions,
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
        public string? ZelleRecipient { get; set; }
        public string? ZelleInstructions { get; set; }
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
            ZelleRecipient = req.ZelleRecipient?.Trim(),
            ZelleInstructions = req.ZelleInstructions?.Trim(),
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
        public string? ZelleRecipient { get; set; }
        public string? ZelleInstructions { get; set; }
    }

    // PUT /api/admin/businesses/{id}
    // Raw ADO.NET because AuthorizeBusinessAsync returns untracked entity
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateBusinessRequest req, CancellationToken ct)
    {
        var biz = await AuthorizeBusinessAsync(id, ct);
        if (biz is null) return Unauthorized();

        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            var name = !string.IsNullOrWhiteSpace(req.Name) ? req.Name.Trim() : biz.Name;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE "Businesses"
                SET "Name" = @name, "Greeting" = @greeting, "Schedule" = @schedule,
                    "Address" = @address, "LogoUrl" = @logo,
                    "PaymentMobileBank" = @pmBank, "PaymentMobileId" = @pmId,
                    "PaymentMobilePhone" = @pmPhone, "NotificationPhone" = @notif,
                    "ZelleRecipient" = @zRecip, "ZelleInstructions" = @zInstr
                WHERE LOWER(CAST("Id" AS TEXT)) = LOWER(@id)
            """;
            AddParam(cmd, "name", name);
            AddParam(cmd, "greeting", (object?)req.Greeting?.Trim() ?? DBNull.Value);
            AddParam(cmd, "schedule", (object?)req.Schedule?.Trim() ?? DBNull.Value);
            AddParam(cmd, "address", (object?)req.Address?.Trim() ?? DBNull.Value);
            AddParam(cmd, "logo", (object?)req.LogoUrl?.Trim() ?? DBNull.Value);
            AddParam(cmd, "pmBank", (object?)req.PaymentMobileBank?.Trim() ?? DBNull.Value);
            AddParam(cmd, "pmId", (object?)req.PaymentMobileId?.Trim() ?? DBNull.Value);
            AddParam(cmd, "pmPhone", (object?)req.PaymentMobilePhone?.Trim() ?? DBNull.Value);
            AddParam(cmd, "notif", (object?)req.NotificationPhone?.Trim() ?? DBNull.Value);
            AddParam(cmd, "zRecip", (object?)req.ZelleRecipient?.Trim() ?? DBNull.Value);
            AddParam(cmd, "zInstr", (object?)req.ZelleInstructions?.Trim() ?? DBNull.Value);
            AddParam(cmd, "id", id.ToString());
            await cmd.ExecuteNonQueryAsync(ct);

            return Ok(new
            {
                id,
                name,
                greeting = req.Greeting?.Trim(),
                schedule = req.Schedule?.Trim(),
                address = req.Address?.Trim(),
                logoUrl = req.LogoUrl?.Trim(),
                paymentMobileBank = req.PaymentMobileBank?.Trim(),
                paymentMobileId = req.PaymentMobileId?.Trim(),
                paymentMobilePhone = req.PaymentMobilePhone?.Trim(),
                notificationPhone = req.NotificationPhone?.Trim(),
                zelleRecipient = req.ZelleRecipient?.Trim(),
                zelleInstructions = req.ZelleInstructions?.Trim()
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Update failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    // PATCH /api/admin/businesses/{id}/toggle — activate/deactivate
    // Raw ADO.NET because IsActive is integer in production
    [HttpPatch("{id:guid}/toggle")]
    public async Task<IActionResult> Toggle(Guid id, CancellationToken ct)
    {
        if (!IsGlobalAdmin())
            return Unauthorized();

        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            using var cmd = conn.CreateCommand();
            // Read current state
            cmd.CommandText = """SELECT "Name", "IsActive" FROM "Businesses" WHERE LOWER(CAST("Id" AS TEXT)) = LOWER(@id) LIMIT 1""";
            AddParam(cmd, "id", id.ToString());
            string? bizName = null; bool currentActive = false;
            using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                if (!await r.ReadAsync(ct))
                    return NotFound(new { error = "Business not found" });
                bizName = r.IsDBNull(0) ? "" : r.GetString(0);
                var rawActive = r["IsActive"];
                currentActive = rawActive switch
                {
                    bool b => b, int i => i != 0, long l => l != 0,
                    string s => s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase),
                    _ => true
                };
            }

            // Toggle
            using var upd = conn.CreateCommand();
            upd.CommandText = """UPDATE "Businesses" SET "IsActive" = @val WHERE LOWER(CAST("Id" AS TEXT)) = LOWER(@id)""";
            AddParam(upd, "val", currentActive ? 0 : 1);
            AddParam(upd, "id", id.ToString());
            await upd.ExecuteNonQueryAsync(ct);

            return Ok(new { id = id.ToString(), name = bizName, isActive = !currentActive });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Toggle failed: {ex.GetType().Name}: {ex.Message}" });
        }
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

    // ── Junk Business Classification ──
    // A business is classified as junk when it matches placeholder patterns
    // created by BusinessResolver auto-onboarding with Meta verification webhooks.

    private static readonly string[] PlaceholderNames = ["Default Business", "Demo Restaurant", "Test", "Test Business"];

    private static (bool IsJunk, string? Reason) ClassifyBusiness(string name, string phoneNumberId, string? accessToken)
    {
        var reasons = new List<string>();
        var n = (name ?? "").Trim();
        var pid = (phoneNumberId ?? "").Trim();

        // Rule 1: Placeholder name
        bool hasPlaceholderName = string.IsNullOrWhiteSpace(n)
            || PlaceholderNames.Any(p => n.Equals(p, StringComparison.OrdinalIgnoreCase))
            || n.StartsWith("Test ", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("Verify", StringComparison.OrdinalIgnoreCase);

        if (hasPlaceholderName)
            reasons.Add("placeholder name: " + (string.IsNullOrWhiteSpace(n) ? "(empty)" : n));

        // Rule 2: Invalid PhoneNumberId (Meta phone number IDs are numeric only)
        bool hasInvalidPhone = string.IsNullOrWhiteSpace(pid)
            || !pid.All(char.IsDigit)
            || pid.Length < 5;

        if (hasInvalidPhone)
            reasons.Add("invalid PhoneNumberId: " + (string.IsNullOrWhiteSpace(pid) ? "(empty)" : pid));

        // Rule 3: Missing access token (can't actually process WhatsApp messages)
        bool hasNoToken = string.IsNullOrWhiteSpace(accessToken);
        if (hasNoToken)
            reasons.Add("no access token");

        // Classification: junk if placeholder name + invalid phone, OR invalid phone + no token
        // Conservative: don't mark as junk if only one weak signal
        bool isJunk = (hasPlaceholderName && hasInvalidPhone)
                   || (hasInvalidPhone && hasNoToken)
                   || (hasPlaceholderName && hasNoToken && hasInvalidPhone);

        return (isJunk, reasons.Count > 0 ? string.Join("; ", reasons) : null);
    }

    // GET /api/admin/businesses/audit — classify all businesses as real or junk
    [HttpGet("audit")]
    public async Task<IActionResult> Audit(CancellationToken ct)
    {
        if (!IsGlobalAdmin())
            return Unauthorized();

        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT b."Id"::text, b."Name", b."PhoneNumberId", b."IsActive", b."AccessToken",
                       b."CreatedAtUtc",
                       (SELECT COUNT(*) FROM "Orders" o WHERE o."BusinessId" = b."Id") AS order_count,
                       (SELECT COUNT(*) FROM "MenuCategories" mc WHERE mc."BusinessId" = b."Id") AS menu_count
                FROM "Businesses" b
                ORDER BY b."CreatedAtUtc" DESC
            """;

            var results = new List<object>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var phoneNumberId = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var rawIsActive = reader["IsActive"];
                var isActive = rawIsActive switch
                {
                    bool bv => bv, int iv => iv != 0, long lv => lv != 0,
                    string sv => sv == "1" || sv.Equals("true", StringComparison.OrdinalIgnoreCase),
                    _ => true
                };
                var token = reader.IsDBNull(4) ? null : reader.GetString(4);
                var created = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5);
                var orderCount = Convert.ToInt32(reader["order_count"]);
                var menuCount = Convert.ToInt32(reader["menu_count"]);

                var (isJunk, junkReason) = ClassifyBusiness(name, phoneNumberId, token);

                // Safety override: never classify as junk if it has real orders
                if (isJunk && orderCount > 0)
                {
                    isJunk = false;
                    junkReason = (junkReason ?? "") + " [OVERRIDE: has " + orderCount + " orders — kept as real]";
                }

                results.Add(new
                {
                    id, name, phoneNumberId, isActive, isJunk, junkReason,
                    orderCount, menuCount,
                    createdAtUtc = created?.ToString("o")
                });
            }

            var junkCount = results.Cast<dynamic>().Count(r => r.isJunk);
            var realCount = results.Count - junkCount;

            return Ok(new { total = results.Count, real = realCount, junk = junkCount, businesses = results });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Audit failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    // POST /api/admin/businesses/cleanup?dryRun=true — deactivate junk businesses
    // Safe: only sets IsActive=0, never deletes. dryRun=true previews without changing anything.
    [HttpPost("cleanup")]
    public async Task<IActionResult> Cleanup([FromQuery] bool dryRun = false, CancellationToken ct = default)
    {
        if (!IsGlobalAdmin())
            return Unauthorized();

        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);

            // Step 1: Read all active businesses and classify
            using var readCmd = conn.CreateCommand();
            readCmd.CommandText = """
                SELECT b."Id"::text, b."Name", b."PhoneNumberId", b."AccessToken",
                       (SELECT COUNT(*) FROM "Orders" o WHERE o."BusinessId" = b."Id") AS order_count
                FROM "Businesses" b
                WHERE b."IsActive"::boolean = true
            """;

            var toDeactivate = new List<(string Id, string Name, string Phone, string Reason)>();
            using (var reader = await readCmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var id = reader.GetString(0);
                    var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    var phone = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    var token = reader.IsDBNull(3) ? null : reader.GetString(3);
                    var orderCount = Convert.ToInt32(reader["order_count"]);

                    var (isJunk, reason) = ClassifyBusiness(name, phone, token);

                    // Safety: never deactivate businesses with real orders
                    if (isJunk && orderCount == 0)
                        toDeactivate.Add((id, name, phone, reason ?? "matched junk pattern"));
                }
            }

            if (dryRun)
            {
                return Ok(new
                {
                    dryRun = true,
                    message = $"Would deactivate {toDeactivate.Count} junk business(es)",
                    affected = toDeactivate.Select(d => new { d.Id, d.Name, phoneNumberId = d.Phone, reason = d.Reason })
                });
            }

            // Step 2: Deactivate
            var deactivated = new List<object>();
            foreach (var (id, name, phone, reason) in toDeactivate)
            {
                using var upd = conn.CreateCommand();
                upd.CommandText = """UPDATE "Businesses" SET "IsActive" = 0 WHERE "Id"::text = @id""";
                var p = upd.CreateParameter();
                p.ParameterName = "id";
                p.Value = id;
                upd.Parameters.Add(p);
                await upd.ExecuteNonQueryAsync(ct);
                deactivated.Add(new { id, name, phoneNumberId = phone, reason });
            }

            return Ok(new
            {
                dryRun = false,
                message = $"Deactivated {deactivated.Count} junk business(es)",
                affected = deactivated
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
