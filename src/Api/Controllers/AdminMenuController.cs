using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Api.Auth;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Controllers;

/// <summary>
/// All menu CRUD uses raw ADO.NET because the production MenuCategories and
/// MenuItems tables store boolean columns (IsActive, IsAvailable) as integer,
/// not boolean. EF would emit "column X is of type integer but expression is
/// of type boolean" on every INSERT/UPDATE/SELECT that touches those columns.
/// </summary>
[ApiController]
[Route("api/admin/menu")]
[EnableRateLimiting("admin")]
public sealed class AdminMenuController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public AdminMenuController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    // ── Helpers ──

    private async Task<System.Data.Common.DbConnection> GetOpenConnectionAsync(CancellationToken ct)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);
        return conn;
    }

    private System.Data.Common.DbParameter MakeParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
        return p;
    }

    private static bool ParseBool(object raw) => raw switch
    {
        bool b => b,
        int i => i != 0,
        long l => l != 0,
        string s => s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase),
        DBNull => true,
        _ => true
    };

    /// <summary>
    /// Auth check: JWT-first (Owner/Manager scoped to business), then X-Admin-Key fallback.
    /// Uses raw ADO.NET for per-business key because the Businesses table has legacy
    /// text/integer column types (from SQLite-generated InitV2 migration).
    /// </summary>
    private async Task<bool> IsAuthorizedForBusinessAsync(Guid businessId, CancellationToken ct)
    {
        // Path 1: JWT with business scope (preferred)
        if (AdminAuth.IsJwtAuthorizedForBusiness(User, businessId))
            return true;

        // Path 2: Global admin key
        if (AdminAuth.IsGlobalAdminKey(Request, _config))
            return true;

        // Path 3: Per-business admin key (legacy)
        if (!Request.Headers.TryGetValue("X-Admin-Key", out var hk) || string.IsNullOrWhiteSpace(hk))
            return false;

        try
        {
            var conn = await GetOpenConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT "AdminKey" FROM "Businesses"
                WHERE "Id"::text = @bid AND "IsActive"::boolean = true
                LIMIT 1
            """;
            MakeParam(cmd, "bid", businessId.ToString());

            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is null or DBNull) return false;

            return SafeEquals(hk.ToString().Trim(), result.ToString()?.Trim() ?? "");
        }
        catch
        {
            return false;
        }
    }

    private bool IsAuthorized()
        => AdminAuth.IsAuthorized(User, Request, _config);

    private static bool SafeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    // ── Categories ──

    // GET /api/admin/menu/categories?businessId=xxx
    [HttpGet("categories")]
    public async Task<IActionResult> ListCategories([FromQuery] Guid businessId, CancellationToken ct)
    {
        try
        {
            if (!await IsAuthorizedForBusinessAsync(businessId, ct)) return Unauthorized();

            var conn = await GetOpenConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT c."Id", c."Name", c."SortOrder", c."IsActive",
                       (SELECT COUNT(*) FROM "MenuItems" i WHERE i."CategoryId" = c."Id") AS "ItemCount"
                FROM "MenuCategories" c
                WHERE c."BusinessId" = @bid
                ORDER BY c."SortOrder"
            """;
            MakeParam(cmd, "bid", businessId);

            var cats = new List<object>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                cats.Add(new
                {
                    id = reader.GetGuid(reader.GetOrdinal("Id")),
                    name = reader.GetString(reader.GetOrdinal("Name")),
                    sortOrder = reader.GetInt32(reader.GetOrdinal("SortOrder")),
                    isActive = ParseBool(reader["IsActive"]),
                    itemCount = Convert.ToInt32(reader["ItemCount"])
                });
            }

            return Ok(cats);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"ListCategories failed: {ex.GetType().Name}: {ex.Message}" });
        }
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
        try
        {
            if (!await IsAuthorizedForBusinessAsync(req.BusinessId, ct)) return Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { error = "Name required" });

            var id = Guid.NewGuid();
            var conn = await GetOpenConnectionAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO "MenuCategories" ("Id", "BusinessId", "Name", "SortOrder", "IsActive", "CreatedAtUtc")
                VALUES (@id, @bid, @name, @sort, @active, @created)
            """;
            MakeParam(cmd, "id", id);
            MakeParam(cmd, "bid", req.BusinessId);
            MakeParam(cmd, "name", req.Name.Trim());
            MakeParam(cmd, "sort", req.SortOrder);
            MakeParam(cmd, "active", req.IsActive ? 1 : 0);
            MakeParam(cmd, "created", DateTime.UtcNow);

            await cmd.ExecuteNonQueryAsync(ct);

            return Ok(new { id, name = req.Name.Trim(), sortOrder = req.SortOrder, isActive = req.IsActive });
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException?.Message ?? ex.Message;
            return StatusCode(500, new { error = $"CreateCategory failed: {ex.GetType().Name}: {inner}" });
        }
    }

    // PUT /api/admin/menu/categories/{id}
    [HttpPut("categories/{id:guid}")]
    public async Task<IActionResult> UpdateCategory(Guid id, [FromBody] CategoryRequest req, CancellationToken ct)
    {
        try
        {
            var conn = await GetOpenConnectionAsync(ct);

            // Read existing to get BusinessId for auth
            using var readCmd = conn.CreateCommand();
            readCmd.CommandText = """SELECT "BusinessId" FROM "MenuCategories" WHERE "Id" = @id LIMIT 1""";
            MakeParam(readCmd, "id", id);
            var bizRaw = await readCmd.ExecuteScalarAsync(ct);
            if (bizRaw is null or DBNull) return NotFound();

            var bizId = (Guid)bizRaw;
            if (!await IsAuthorizedForBusinessAsync(bizId, ct)) return Unauthorized();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE "MenuCategories"
                SET "Name" = @name, "SortOrder" = @sort, "IsActive" = @active
                WHERE "Id" = @id
            """;
            MakeParam(cmd, "name", string.IsNullOrWhiteSpace(req.Name) ? "" : req.Name.Trim());
            MakeParam(cmd, "sort", req.SortOrder);
            MakeParam(cmd, "active", req.IsActive ? 1 : 0);
            MakeParam(cmd, "id", id);

            await cmd.ExecuteNonQueryAsync(ct);

            return Ok(new { id, name = req.Name?.Trim(), sortOrder = req.SortOrder, isActive = req.IsActive });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"UpdateCategory failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    // DELETE /api/admin/menu/categories/{id}
    [HttpDelete("categories/{id:guid}")]
    public async Task<IActionResult> DeleteCategory(Guid id, CancellationToken ct)
    {
        try
        {
            var conn = await GetOpenConnectionAsync(ct);

            // Read BusinessId for auth
            using var readCmd = conn.CreateCommand();
            readCmd.CommandText = """SELECT "BusinessId" FROM "MenuCategories" WHERE "Id" = @id LIMIT 1""";
            MakeParam(readCmd, "id", id);
            var bizRaw = await readCmd.ExecuteScalarAsync(ct);
            if (bizRaw is null or DBNull) return NotFound();

            if (!await IsAuthorizedForBusinessAsync((Guid)bizRaw, ct)) return Unauthorized();

            // Cascade: delete aliases → items → category
            using var delAliases = conn.CreateCommand();
            delAliases.CommandText = """
                DELETE FROM "MenuItemAliases"
                WHERE "MenuItemId" IN (SELECT "Id" FROM "MenuItems" WHERE "CategoryId" = @cid)
            """;
            MakeParam(delAliases, "cid", id);
            await delAliases.ExecuteNonQueryAsync(ct);

            using var delItems = conn.CreateCommand();
            delItems.CommandText = """DELETE FROM "MenuItems" WHERE "CategoryId" = @cid""";
            MakeParam(delItems, "cid", id);
            await delItems.ExecuteNonQueryAsync(ct);

            using var delCat = conn.CreateCommand();
            delCat.CommandText = """DELETE FROM "MenuCategories" WHERE "Id" = @id""";
            MakeParam(delCat, "id", id);
            await delCat.ExecuteNonQueryAsync(ct);

            return Ok(new { deleted = true });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"DeleteCategory failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    // ── Items ──

    // GET /api/admin/menu/items?categoryId=xxx or businessId=xxx
    [HttpGet("items")]
    public async Task<IActionResult> ListItems([FromQuery] Guid? categoryId, [FromQuery] Guid? businessId, CancellationToken ct)
    {
        try
        {
            if (businessId.HasValue)
            {
                if (!await IsAuthorizedForBusinessAsync(businessId.Value, ct)) return Unauthorized();
            }
            else if (!IsAuthorized()) return Unauthorized();

            var conn = await GetOpenConnectionAsync(ct);
            using var cmd = conn.CreateCommand();

            if (categoryId.HasValue)
            {
                cmd.CommandText = """
                    SELECT i."Id", i."Name", i."Price", i."Description", i."IsAvailable",
                           i."SortOrder", i."CategoryId", c."Name" AS "CategoryName", c."SortOrder" AS "CatSort"
                    FROM "MenuItems" i
                    JOIN "MenuCategories" c ON c."Id" = i."CategoryId"
                    WHERE i."CategoryId" = @cid
                    ORDER BY c."SortOrder", i."SortOrder"
                """;
                MakeParam(cmd, "cid", categoryId.Value);
            }
            else if (businessId.HasValue)
            {
                cmd.CommandText = """
                    SELECT i."Id", i."Name", i."Price", i."Description", i."IsAvailable",
                           i."SortOrder", i."CategoryId", c."Name" AS "CategoryName", c."SortOrder" AS "CatSort"
                    FROM "MenuItems" i
                    JOIN "MenuCategories" c ON c."Id" = i."CategoryId"
                    WHERE c."BusinessId" = @bid
                    ORDER BY c."SortOrder", i."SortOrder"
                """;
                MakeParam(cmd, "bid", businessId.Value);
            }
            else
            {
                cmd.CommandText = """
                    SELECT i."Id", i."Name", i."Price", i."Description", i."IsAvailable",
                           i."SortOrder", i."CategoryId", c."Name" AS "CategoryName", c."SortOrder" AS "CatSort"
                    FROM "MenuItems" i
                    JOIN "MenuCategories" c ON c."Id" = i."CategoryId"
                    ORDER BY c."SortOrder", i."SortOrder"
                """;
            }

            // Collect item IDs to batch-load aliases
            var itemRows = new List<(Guid Id, string Name, decimal Price, string? Description,
                                     bool IsAvailable, int SortOrder, Guid CategoryId, string CategoryName)>();
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var descOrd = reader.GetOrdinal("Description");
                itemRows.Add((
                    reader.GetGuid(reader.GetOrdinal("Id")),
                    reader.GetString(reader.GetOrdinal("Name")),
                    reader.GetDecimal(reader.GetOrdinal("Price")),
                    reader.IsDBNull(descOrd) ? null : reader.GetString(descOrd),
                    ParseBool(reader["IsAvailable"]),
                    reader.GetInt32(reader.GetOrdinal("SortOrder")),
                    reader.GetGuid(reader.GetOrdinal("CategoryId")),
                    reader.GetString(reader.GetOrdinal("CategoryName"))
                ));
            }

            // Load aliases for all items in one query
            var aliases = new Dictionary<Guid, List<object>>();
            if (itemRows.Count > 0)
            {
                using var aliasCmd = conn.CreateCommand();
                // Build IN clause with parameters
                var paramNames = new List<string>();
                for (var idx = 0; idx < itemRows.Count; idx++)
                {
                    var pn = $"@iid{idx}";
                    paramNames.Add(pn);
                    MakeParam(aliasCmd, $"iid{idx}", itemRows[idx].Id);
                }
                aliasCmd.CommandText = $"""
                    SELECT "Id", "MenuItemId", "Alias"
                    FROM "MenuItemAliases"
                    WHERE "MenuItemId" IN ({string.Join(",", paramNames)})
                """;
                using var ar = await aliasCmd.ExecuteReaderAsync(ct);
                while (await ar.ReadAsync(ct))
                {
                    var mid = ar.GetGuid(ar.GetOrdinal("MenuItemId"));
                    if (!aliases.ContainsKey(mid)) aliases[mid] = new List<object>();
                    aliases[mid].Add(new
                    {
                        id = ar.GetGuid(ar.GetOrdinal("Id")),
                        alias = ar.GetString(ar.GetOrdinal("Alias"))
                    });
                }
            }

            var items = itemRows.Select(r => new
            {
                id = r.Id,
                name = r.Name,
                price = r.Price,
                description = r.Description,
                isAvailable = r.IsAvailable,
                sortOrder = r.SortOrder,
                categoryId = r.CategoryId,
                categoryName = r.CategoryName,
                aliases = aliases.TryGetValue(r.Id, out var a) ? a : new List<object>()
            });

            return Ok(items);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"ListItems failed: {ex.GetType().Name}: {ex.Message}" });
        }
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
        try
        {
            // Resolve businessId from category using raw SQL
            var conn = await GetOpenConnectionAsync(ct);
            using var catCmd = conn.CreateCommand();
            catCmd.CommandText = """SELECT "BusinessId" FROM "MenuCategories" WHERE "Id" = @cid LIMIT 1""";
            MakeParam(catCmd, "cid", req.CategoryId);
            var catBizRaw = await catCmd.ExecuteScalarAsync(ct);
            if (catBizRaw is null or DBNull) return BadRequest(new { error = "Invalid categoryId" });

            var catBiz = (Guid)catBizRaw;
            if (!await IsAuthorizedForBusinessAsync(catBiz, ct)) return Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { error = "Name required" });

            var id = Guid.NewGuid();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO "MenuItems" ("Id", "CategoryId", "Name", "Price", "Description", "IsAvailable", "SortOrder", "CreatedAtUtc")
                VALUES (@id, @cid, @name, @price, @desc, @avail, @sort, @created)
            """;
            MakeParam(cmd, "id", id);
            MakeParam(cmd, "cid", req.CategoryId);
            MakeParam(cmd, "name", req.Name.Trim());
            MakeParam(cmd, "price", req.Price);
            MakeParam(cmd, "desc", (object?)req.Description?.Trim() ?? DBNull.Value);
            MakeParam(cmd, "avail", req.IsAvailable ? 1 : 0);
            MakeParam(cmd, "sort", req.SortOrder);
            MakeParam(cmd, "created", DateTime.UtcNow);

            await cmd.ExecuteNonQueryAsync(ct);

            // Insert aliases
            var aliasResults = new List<object>();
            if (req.Aliases is { Count: > 0 })
            {
                foreach (var alias in req.Aliases.Where(a => !string.IsNullOrWhiteSpace(a)))
                {
                    var aid = Guid.NewGuid();
                    using var aliasCmd = conn.CreateCommand();
                    aliasCmd.CommandText = """
                        INSERT INTO "MenuItemAliases" ("Id", "MenuItemId", "Alias")
                        VALUES (@aid, @mid, @alias)
                    """;
                    MakeParam(aliasCmd, "aid", aid);
                    MakeParam(aliasCmd, "mid", id);
                    MakeParam(aliasCmd, "alias", alias.Trim().ToLowerInvariant());
                    await aliasCmd.ExecuteNonQueryAsync(ct);
                    aliasResults.Add(new { id = aid, alias = alias.Trim().ToLowerInvariant() });
                }
            }

            return Ok(new
            {
                id,
                name = req.Name.Trim(),
                price = req.Price,
                description = req.Description?.Trim(),
                isAvailable = req.IsAvailable,
                sortOrder = req.SortOrder,
                aliases = aliasResults
            });
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException?.Message ?? ex.Message;
            return StatusCode(500, new { error = $"CreateItem failed: {ex.GetType().Name}: {inner}" });
        }
    }

    // PUT /api/admin/menu/items/{id}
    [HttpPut("items/{id:guid}")]
    public async Task<IActionResult> UpdateItem(Guid id, [FromBody] ItemRequest req, CancellationToken ct)
    {
        try
        {
            var conn = await GetOpenConnectionAsync(ct);

            // Get item's category → business for auth
            using var readCmd = conn.CreateCommand();
            readCmd.CommandText = """
                SELECT c."BusinessId"
                FROM "MenuItems" i JOIN "MenuCategories" c ON c."Id" = i."CategoryId"
                WHERE i."Id" = @id LIMIT 1
            """;
            MakeParam(readCmd, "id", id);
            var bizRaw = await readCmd.ExecuteScalarAsync(ct);
            if (bizRaw is null or DBNull) return NotFound();

            if (!await IsAuthorizedForBusinessAsync((Guid)bizRaw, ct)) return Unauthorized();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE "MenuItems"
                SET "Name" = @name, "Price" = @price, "Description" = @desc,
                    "IsAvailable" = @avail, "SortOrder" = @sort
                WHERE "Id" = @id
            """;
            MakeParam(cmd, "name", string.IsNullOrWhiteSpace(req.Name) ? "" : req.Name.Trim());
            MakeParam(cmd, "price", req.Price);
            MakeParam(cmd, "desc", (object?)req.Description?.Trim() ?? DBNull.Value);
            MakeParam(cmd, "avail", req.IsAvailable ? 1 : 0);
            MakeParam(cmd, "sort", req.SortOrder);
            MakeParam(cmd, "id", id);

            await cmd.ExecuteNonQueryAsync(ct);

            // Replace aliases if provided
            if (req.Aliases is not null)
            {
                using var delCmd = conn.CreateCommand();
                delCmd.CommandText = """DELETE FROM "MenuItemAliases" WHERE "MenuItemId" = @mid""";
                MakeParam(delCmd, "mid", id);
                await delCmd.ExecuteNonQueryAsync(ct);

                foreach (var alias in req.Aliases.Where(a => !string.IsNullOrWhiteSpace(a)))
                {
                    using var insCmd = conn.CreateCommand();
                    insCmd.CommandText = """
                        INSERT INTO "MenuItemAliases" ("Id", "MenuItemId", "Alias")
                        VALUES (@aid, @mid, @alias)
                    """;
                    MakeParam(insCmd, "aid", Guid.NewGuid());
                    MakeParam(insCmd, "mid", id);
                    MakeParam(insCmd, "alias", alias.Trim().ToLowerInvariant());
                    await insCmd.ExecuteNonQueryAsync(ct);
                }
            }

            // Read back aliases
            using var aliasCmd = conn.CreateCommand();
            aliasCmd.CommandText = """SELECT "Id", "Alias" FROM "MenuItemAliases" WHERE "MenuItemId" = @mid""";
            MakeParam(aliasCmd, "mid", id);
            var aliases = new List<object>();
            using var ar = await aliasCmd.ExecuteReaderAsync(ct);
            while (await ar.ReadAsync(ct))
            {
                aliases.Add(new { id = ar.GetGuid(0), alias = ar.GetString(1) });
            }

            return Ok(new
            {
                id,
                name = req.Name?.Trim(),
                price = req.Price,
                description = req.Description?.Trim(),
                isAvailable = req.IsAvailable,
                sortOrder = req.SortOrder,
                aliases
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"UpdateItem failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    // DELETE /api/admin/menu/items/{id}
    [HttpDelete("items/{id:guid}")]
    public async Task<IActionResult> DeleteItem(Guid id, CancellationToken ct)
    {
        try
        {
            var conn = await GetOpenConnectionAsync(ct);

            // Auth check
            using var readCmd = conn.CreateCommand();
            readCmd.CommandText = """
                SELECT c."BusinessId"
                FROM "MenuItems" i JOIN "MenuCategories" c ON c."Id" = i."CategoryId"
                WHERE i."Id" = @id LIMIT 1
            """;
            MakeParam(readCmd, "id", id);
            var bizRaw = await readCmd.ExecuteScalarAsync(ct);
            if (bizRaw is null or DBNull) return NotFound();

            if (!await IsAuthorizedForBusinessAsync((Guid)bizRaw, ct)) return Unauthorized();

            // Cascade: aliases → item
            using var delAliases = conn.CreateCommand();
            delAliases.CommandText = """DELETE FROM "MenuItemAliases" WHERE "MenuItemId" = @mid""";
            MakeParam(delAliases, "mid", id);
            await delAliases.ExecuteNonQueryAsync(ct);

            using var delItem = conn.CreateCommand();
            delItem.CommandText = """DELETE FROM "MenuItems" WHERE "Id" = @id""";
            MakeParam(delItem, "id", id);
            await delItem.ExecuteNonQueryAsync(ct);

            return Ok(new { deleted = true });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"DeleteItem failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    // PATCH /api/admin/menu/items/{id}/availability
    [HttpPatch("items/{id:guid}/availability")]
    public async Task<IActionResult> ToggleAvailability(Guid id, [FromBody] AvailabilityRequest req, CancellationToken ct)
    {
        try
        {
            var conn = await GetOpenConnectionAsync(ct);

            // Auth check + get name for response
            using var readCmd = conn.CreateCommand();
            readCmd.CommandText = """
                SELECT i."Name", c."BusinessId"
                FROM "MenuItems" i JOIN "MenuCategories" c ON c."Id" = i."CategoryId"
                WHERE i."Id" = @id LIMIT 1
            """;
            MakeParam(readCmd, "id", id);
            using var rr = await readCmd.ExecuteReaderAsync(ct);
            if (!await rr.ReadAsync(ct)) return NotFound();
            var itemName = rr.GetString(0);
            var bizId = rr.GetGuid(1);
            await rr.CloseAsync();

            if (!await IsAuthorizedForBusinessAsync(bizId, ct)) return Unauthorized();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """UPDATE "MenuItems" SET "IsAvailable" = @avail WHERE "Id" = @id""";
            MakeParam(cmd, "avail", req.IsAvailable ? 1 : 0);
            MakeParam(cmd, "id", id);
            await cmd.ExecuteNonQueryAsync(ct);

            return Ok(new { id, name = itemName, isAvailable = req.IsAvailable });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"ToggleAvailability failed: {ex.GetType().Name}: {ex.Message}" });
        }
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
        try
        {
            if (string.IsNullOrWhiteSpace(req.Alias)) return BadRequest(new { error = "Alias required" });

            var conn = await GetOpenConnectionAsync(ct);

            // Auth
            using var readCmd = conn.CreateCommand();
            readCmd.CommandText = """
                SELECT c."BusinessId"
                FROM "MenuItems" i JOIN "MenuCategories" c ON c."Id" = i."CategoryId"
                WHERE i."Id" = @id LIMIT 1
            """;
            MakeParam(readCmd, "id", itemId);
            var bizRaw = await readCmd.ExecuteScalarAsync(ct);
            if (bizRaw is null or DBNull) return NotFound();

            if (!await IsAuthorizedForBusinessAsync((Guid)bizRaw, ct)) return Unauthorized();

            var id = Guid.NewGuid();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO "MenuItemAliases" ("Id", "MenuItemId", "Alias")
                VALUES (@id, @mid, @alias)
            """;
            MakeParam(cmd, "id", id);
            MakeParam(cmd, "mid", itemId);
            MakeParam(cmd, "alias", req.Alias.Trim().ToLowerInvariant());
            await cmd.ExecuteNonQueryAsync(ct);

            return Ok(new { id, alias = req.Alias.Trim().ToLowerInvariant(), menuItemId = itemId });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"AddAlias failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }

    // DELETE /api/admin/menu/aliases/{id}
    [HttpDelete("aliases/{id:guid}")]
    public async Task<IActionResult> DeleteAlias(Guid id, CancellationToken ct)
    {
        try
        {
            var conn = await GetOpenConnectionAsync(ct);

            // Get business for auth
            using var readCmd = conn.CreateCommand();
            readCmd.CommandText = """
                SELECT c."BusinessId"
                FROM "MenuItemAliases" a
                JOIN "MenuItems" i ON i."Id" = a."MenuItemId"
                JOIN "MenuCategories" c ON c."Id" = i."CategoryId"
                WHERE a."Id" = @id LIMIT 1
            """;
            MakeParam(readCmd, "id", id);
            var bizRaw = await readCmd.ExecuteScalarAsync(ct);
            if (bizRaw is null or DBNull) return NotFound();

            if (!await IsAuthorizedForBusinessAsync((Guid)bizRaw, ct)) return Unauthorized();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """DELETE FROM "MenuItemAliases" WHERE "Id" = @id""";
            MakeParam(cmd, "id", id);
            await cmd.ExecuteNonQueryAsync(ct);

            return Ok(new { deleted = true });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"DeleteAlias failed: {ex.GetType().Name}: {ex.Message}" });
        }
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
        try
        {
            var conn = await GetOpenConnectionAsync(ct);

            // Load active categories
            using var catCmd = conn.CreateCommand();
            catCmd.CommandText = """
                SELECT "Id", "Name"
                FROM "MenuCategories"
                WHERE "BusinessId" = @bid AND "IsActive"::boolean = true
                ORDER BY "SortOrder"
            """;
            MakeParam(catCmd, "bid", businessId);

            var categories = new List<(Guid Id, string Name)>();
            using var cr = await catCmd.ExecuteReaderAsync(ct);
            while (await cr.ReadAsync(ct))
            {
                categories.Add((cr.GetGuid(0), cr.GetString(1)));
            }
            await cr.CloseAsync();

            if (categories.Count == 0)
                return Ok(Array.Empty<object>());

            // Load available items for those categories
            var catParamNames = new List<string>();
            using var itemCmd = conn.CreateCommand();
            for (var idx = 0; idx < categories.Count; idx++)
            {
                var pn = $"@cat{idx}";
                catParamNames.Add(pn);
                MakeParam(itemCmd, $"cat{idx}", categories[idx].Id);
            }
            itemCmd.CommandText = $"""
                SELECT "CategoryId", "Name", "Price", "Description"
                FROM "MenuItems"
                WHERE "CategoryId" IN ({string.Join(",", catParamNames)})
                  AND "IsAvailable"::boolean = true
                ORDER BY "SortOrder"
            """;

            var itemsByCategory = new Dictionary<Guid, List<object>>();
            using var ir = await itemCmd.ExecuteReaderAsync(ct);
            while (await ir.ReadAsync(ct))
            {
                var cid = ir.GetGuid(0);
                if (!itemsByCategory.ContainsKey(cid)) itemsByCategory[cid] = new List<object>();
                var descOrd = ir.GetOrdinal("Description");
                itemsByCategory[cid].Add(new
                {
                    name = ir.GetString(1),
                    price = ir.GetDecimal(2),
                    description = ir.IsDBNull(descOrd) ? null : ir.GetString(descOrd)
                });
            }

            var result = categories.Select(c => new
            {
                name = c.Name,
                items = itemsByCategory.TryGetValue(c.Id, out var items) ? items : new List<object>()
            });

            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"PublicMenu failed: {ex.GetType().Name}: {ex.Message}" });
        }
    }
}
