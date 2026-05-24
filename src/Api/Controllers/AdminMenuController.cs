using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
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
[Authorize]
[EnableRateLimiting("admin")]
public sealed class AdminMenuController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<AdminMenuController> _logger;

    public AdminMenuController(AppDbContext db, IConfiguration config, ILogger<AdminMenuController> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
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
            _logger.LogError(ex, "Admin menu endpoint error");
            return StatusCode(500, new { error = $"ListCategories failed: Unexpected server error" });
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
            _logger.LogError(ex, "Admin menu endpoint error");
            return StatusCode(500, new { error = $"CreateCategory failed: Unexpected server error" });
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
            _logger.LogError(ex, "Admin menu endpoint error");
            return StatusCode(500, new { error = $"UpdateCategory failed: Unexpected server error" });
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
            _logger.LogError(ex, "Admin menu endpoint error");
            return StatusCode(500, new { error = $"DeleteCategory failed: Unexpected server error" });
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
            using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
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
            } // reader disposed here — connection free for alias query

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
            _logger.LogError(ex, "Admin menu endpoint error");
            return StatusCode(500, new { error = $"ListItems failed: Unexpected server error" });
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
            _logger.LogError(ex, "Admin menu endpoint error");
            return StatusCode(500, new { error = $"CreateItem failed: Unexpected server error" });
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
            _logger.LogError(ex, "Admin menu endpoint error");
            return StatusCode(500, new { error = $"UpdateItem failed: Unexpected server error" });
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
            _logger.LogError(ex, "Admin menu endpoint error");
            return StatusCode(500, new { error = $"DeleteItem failed: Unexpected server error" });
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
            _logger.LogError(ex, "Admin menu endpoint error");
            return StatusCode(500, new { error = $"ToggleAvailability failed: Unexpected server error" });
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
            _logger.LogError(ex, "Admin menu endpoint error");
            return StatusCode(500, new { error = $"AddAlias failed: Unexpected server error" });
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
            _logger.LogError(ex, "Admin menu endpoint error");
            return StatusCode(500, new { error = $"DeleteAlias failed: Unexpected server error" });
        }
    }

    public sealed class AliasRequest
    {
        public string Alias { get; set; } = "";
    }

    // ── Bulk Import ──

    public sealed class BulkImportRequest
    {
        public Guid BusinessId { get; set; }
        public string Mode { get; set; } = "text"; // "text" or "json"
        public string RawInput { get; set; } = "";
    }

    private sealed class ParsedCategory
    {
        public string Name { get; set; } = "";
        public List<ParsedItem> Items { get; set; } = new();
    }

    private sealed class ParsedItem
    {
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
        public string? Description { get; set; }
        public List<string> Aliases { get; set; } = new();
    }

    private sealed class ParsedExtra
    {
        public string Name { get; set; } = "";
        public decimal? Price { get; set; }
        public List<string> ProductNames { get; set; } = new();
        public List<string> CategoryNames { get; set; } = new();
    }

    private sealed class ParsedUpsell
    {
        public string SourceCategoryName { get; set; } = "";
        public string SuggestedItemName { get; set; } = "";
    }

    // POST /api/admin/menu/import
    [HttpPost("import")]
    public async Task<IActionResult> BulkImport([FromBody] BulkImportRequest req, CancellationToken ct)
    {
        try
        {
            if (!await IsAuthorizedForBusinessAsync(req.BusinessId, ct)) return Unauthorized();
            if (string.IsNullOrWhiteSpace(req.RawInput)) return BadRequest(new { error = "rawInput is empty" });

            var categories = new List<ParsedCategory>();
            var extras = new List<ParsedExtra>();
            var upsells = new List<ParsedUpsell>();
            var warnings = new List<string>();

            if (req.Mode == "json")
                ParseJsonInput(req.RawInput, categories, warnings);
            else
                ParseTextInput(req.RawInput, categories, extras, upsells, warnings);

            if (categories.Count == 0 && extras.Count == 0 && upsells.Count == 0)
                return BadRequest(new { error = "No categories, extras, or upsells detected", warnings });

            // Execute import
            var conn = await GetOpenConnectionAsync(ct);
            int categoriesCreated = 0, itemsCreated = 0, aliasesCreated = 0, skipped = 0;

            foreach (var cat in categories)
            {
                if (string.IsNullOrWhiteSpace(cat.Name))
                {
                    warnings.Add("Skipped category with empty name");
                    continue;
                }

                // Check if category already exists for this business
                Guid categoryId;
                using (var findCat = conn.CreateCommand())
                {
                    findCat.CommandText = """
                        SELECT "Id" FROM "MenuCategories"
                        WHERE "BusinessId" = @bid AND LOWER("Name") = LOWER(@name)
                        LIMIT 1
                    """;
                    MakeParam(findCat, "bid", req.BusinessId);
                    MakeParam(findCat, "name", cat.Name.Trim());
                    var existing = await findCat.ExecuteScalarAsync(ct);
                    if (existing is not null and not DBNull)
                    {
                        categoryId = (Guid)existing;
                    }
                    else
                    {
                        categoryId = Guid.NewGuid();
                        using var insCat = conn.CreateCommand();
                        insCat.CommandText = """
                            INSERT INTO "MenuCategories" ("Id", "BusinessId", "Name", "SortOrder", "IsActive", "CreatedAtUtc")
                            VALUES (@id, @bid, @name, @sort, @active, @created)
                        """;
                        MakeParam(insCat, "id", categoryId);
                        MakeParam(insCat, "bid", req.BusinessId);
                        MakeParam(insCat, "name", cat.Name.Trim());
                        MakeParam(insCat, "sort", categoriesCreated);
                        MakeParam(insCat, "active", 1);
                        MakeParam(insCat, "created", DateTime.UtcNow);
                        await insCat.ExecuteNonQueryAsync(ct);
                        categoriesCreated++;
                    }
                }

                // Import items
                foreach (var item in cat.Items)
                {
                    if (string.IsNullOrWhiteSpace(item.Name))
                    {
                        warnings.Add($"Skipped item with empty name in category '{cat.Name}'");
                        continue;
                    }

                    // Check duplicate
                    using var findItem = conn.CreateCommand();
                    findItem.CommandText = """
                        SELECT "Id" FROM "MenuItems"
                        WHERE "CategoryId" = @cid AND LOWER("Name") = LOWER(@name)
                        LIMIT 1
                    """;
                    MakeParam(findItem, "cid", categoryId);
                    MakeParam(findItem, "name", item.Name.Trim());
                    var existingItem = await findItem.ExecuteScalarAsync(ct);
                    if (existingItem is not null and not DBNull)
                    {
                        skipped++;
                        warnings.Add($"Duplicate skipped: '{item.Name}' in '{cat.Name}'");
                        continue;
                    }

                    var itemId = Guid.NewGuid();
                    using var insItem = conn.CreateCommand();
                    insItem.CommandText = """
                        INSERT INTO "MenuItems" ("Id", "CategoryId", "Name", "Price", "Description", "IsAvailable", "SortOrder", "CreatedAtUtc")
                        VALUES (@id, @cid, @name, @price, @desc, @avail, @sort, @created)
                    """;
                    MakeParam(insItem, "id", itemId);
                    MakeParam(insItem, "cid", categoryId);
                    MakeParam(insItem, "name", item.Name.Trim());
                    MakeParam(insItem, "price", item.Price);
                    MakeParam(insItem, "desc", (object?)item.Description?.Trim() ?? DBNull.Value);
                    MakeParam(insItem, "avail", 1);
                    MakeParam(insItem, "sort", itemsCreated);
                    MakeParam(insItem, "created", DateTime.UtcNow);
                    await insItem.ExecuteNonQueryAsync(ct);
                    itemsCreated++;

                    // Import aliases
                    foreach (var alias in item.Aliases.Where(a => !string.IsNullOrWhiteSpace(a)))
                    {
                        using var insAlias = conn.CreateCommand();
                        insAlias.CommandText = """
                            INSERT INTO "MenuItemAliases" ("Id", "MenuItemId", "Alias")
                            VALUES (@aid, @mid, @alias)
                        """;
                        MakeParam(insAlias, "aid", Guid.NewGuid());
                        MakeParam(insAlias, "mid", itemId);
                        MakeParam(insAlias, "alias", alias.Trim().ToLowerInvariant());
                        await insAlias.ExecuteNonQueryAsync(ct);
                        aliasesCreated++;
                    }
                }
            }

            // ── Import Extras ──
            int extrasCreated = 0;
            foreach (var ext in extras)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(ext.Name)) continue;
                    var extraId = Guid.NewGuid();
                    using var insExtra = conn.CreateCommand();
                    insExtra.CommandText = """
                        INSERT INTO "Extras" ("Id","BusinessId","Name","AdditivePrice","IsActive","SortOrder","CreatedAtUtc")
                        VALUES (@id,@bid,@name,@price,true,@sort,@created)
                    """;
                    MakeParam(insExtra, "id", extraId);
                    MakeParam(insExtra, "bid", req.BusinessId);
                    MakeParam(insExtra, "name", ext.Name.Trim());
                    MakeParam(insExtra, "price", ext.Price.HasValue ? (object)ext.Price.Value : DBNull.Value);
                    MakeParam(insExtra, "sort", extrasCreated);
                    MakeParam(insExtra, "created", DateTime.UtcNow);
                    await insExtra.ExecuteNonQueryAsync(ct);

                    // Link to products by name
                    foreach (var prodName in ext.ProductNames)
                    {
                        using var findItem = conn.CreateCommand();
                        findItem.CommandText = """
                            SELECT i."Id" FROM "MenuItems" i
                            JOIN "MenuCategories" c ON c."Id" = i."CategoryId"
                            WHERE c."BusinessId" = @bid AND LOWER(i."Name") = LOWER(@name)
                            LIMIT 1
                        """;
                        MakeParam(findItem, "bid", req.BusinessId);
                        MakeParam(findItem, "name", prodName.Trim());
                        var itemId = await findItem.ExecuteScalarAsync(ct);
                        if (itemId is not null and not DBNull)
                        {
                            using var link = conn.CreateCommand();
                            link.CommandText = """
                                INSERT INTO "ExtraMenuItems" ("Id","ExtraId","MenuItemId")
                                VALUES (@id,@eid,@mid)
                            """;
                            MakeParam(link, "id", Guid.NewGuid());
                            MakeParam(link, "eid", extraId);
                            MakeParam(link, "mid", (Guid)itemId);
                            await link.ExecuteNonQueryAsync(ct);
                        }
                        else
                        {
                            warnings.Add($"Extra '{ext.Name}': product '{prodName.Trim()}' not found");
                        }
                    }

                    // Link to categories by name
                    foreach (var catName in ext.CategoryNames)
                    {
                        using var findCat = conn.CreateCommand();
                        findCat.CommandText = """
                            SELECT "Id" FROM "MenuCategories"
                            WHERE "BusinessId" = @bid AND LOWER("Name") = LOWER(@name)
                            LIMIT 1
                        """;
                        MakeParam(findCat, "bid", req.BusinessId);
                        MakeParam(findCat, "name", catName.Trim());
                        var catId = await findCat.ExecuteScalarAsync(ct);
                        if (catId is not null and not DBNull)
                        {
                            using var link = conn.CreateCommand();
                            link.CommandText = """
                                INSERT INTO "ExtraMenuCategories" ("Id","ExtraId","MenuCategoryId")
                                VALUES (@id,@eid,@cid)
                            """;
                            MakeParam(link, "id", Guid.NewGuid());
                            MakeParam(link, "eid", extraId);
                            MakeParam(link, "cid", (Guid)catId);
                            await link.ExecuteNonQueryAsync(ct);
                        }
                        else
                        {
                            warnings.Add($"Extra '{ext.Name}': category '{catName.Trim()}' not found");
                        }
                    }

                    extrasCreated++;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Extra '{ext.Name}' failed: {ex.Message}");
                }
            }

            // ── Import Upsells ──
            int upsellsCreated = 0;
            foreach (var ups in upsells)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(ups.SourceCategoryName)) continue;

                    // Find source category
                    using var findSrcCat = conn.CreateCommand();
                    findSrcCat.CommandText = """
                        SELECT "Id" FROM "MenuCategories"
                        WHERE "BusinessId" = @bid AND LOWER("Name") = LOWER(@name)
                        LIMIT 1
                    """;
                    MakeParam(findSrcCat, "bid", req.BusinessId);
                    MakeParam(findSrcCat, "name", ups.SourceCategoryName.Trim());
                    var srcCatId = await findSrcCat.ExecuteScalarAsync(ct);
                    if (srcCatId is null or DBNull)
                    {
                        warnings.Add($"Upsell: source category '{ups.SourceCategoryName.Trim()}' not found");
                        continue;
                    }

                    // Find suggested item
                    Guid? suggestedItemId = null;
                    string? suggestionLabel = null;
                    if (!string.IsNullOrWhiteSpace(ups.SuggestedItemName))
                    {
                        using var findItem = conn.CreateCommand();
                        findItem.CommandText = """
                            SELECT i."Id" FROM "MenuItems" i
                            JOIN "MenuCategories" c ON c."Id" = i."CategoryId"
                            WHERE c."BusinessId" = @bid AND LOWER(i."Name") = LOWER(@name)
                            LIMIT 1
                        """;
                        MakeParam(findItem, "bid", req.BusinessId);
                        MakeParam(findItem, "name", ups.SuggestedItemName.Trim());
                        var itemId = await findItem.ExecuteScalarAsync(ct);
                        if (itemId is not null and not DBNull)
                            suggestedItemId = (Guid)itemId;
                        else
                            suggestionLabel = ups.SuggestedItemName.Trim();
                    }

                    using var insUpsell = conn.CreateCommand();
                    insUpsell.CommandText = """
                        INSERT INTO "UpsellRules" ("Id","BusinessId","SourceCategoryId","SuggestedMenuItemId","SuggestionLabel","IsActive","SortOrder","CreatedAtUtc")
                        VALUES (@id,@bid,@srcCat,@sugItem,@label,true,@sort,@created)
                    """;
                    MakeParam(insUpsell, "id", Guid.NewGuid());
                    MakeParam(insUpsell, "bid", req.BusinessId);
                    MakeParam(insUpsell, "srcCat", (Guid)srcCatId);
                    MakeParam(insUpsell, "sugItem", suggestedItemId.HasValue ? (object)suggestedItemId.Value : DBNull.Value);
                    MakeParam(insUpsell, "label", (object?)suggestionLabel ?? DBNull.Value);
                    MakeParam(insUpsell, "sort", upsellsCreated);
                    MakeParam(insUpsell, "created", DateTime.UtcNow);
                    await insUpsell.ExecuteNonQueryAsync(ct);
                    upsellsCreated++;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Upsell '{ups.SourceCategoryName} -> {ups.SuggestedItemName}' failed: {ex.Message}");
                }
            }

            return Ok(new
            {
                categoriesCreated,
                itemsCreated,
                aliasesCreated,
                extrasCreated,
                upsellsCreated,
                skipped,
                warnings
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin menu endpoint error");
            return StatusCode(500, new { error = $"BulkImport failed: Unexpected server error" });
        }
    }

    // ── Reset Menu ──

    public sealed class ResetMenuRequest
    {
        public Guid BusinessId { get; set; }
        public string Confirm { get; set; } = "";
    }

    private const string ResetMenuConfirmString = "RESET MENU";

    /// <summary>
    /// Wipes the entire menu surface for one business in a single transaction:
    /// MenuCategories + MenuItems + MenuItemAliases + Extras (+ extra link rows)
    /// + UpsellRules. Does NOT touch Orders, OrderItems, Customers,
    /// ConversationStates, MenuPdfs, Business settings, BusinessUsers, or any
    /// payments / analytics row. Scoped strictly by BusinessId.
    ///
    /// Use ?dryRun=true to get would-delete counts without performing any writes.
    /// A real (non-dryRun) call requires <see cref="ResetMenuRequest.Confirm"/>
    /// to be exactly "RESET MENU".
    /// </summary>
    [HttpPost("reset")]
    public async Task<IActionResult> ResetMenu(
        [FromBody] ResetMenuRequest req,
        [FromQuery] bool dryRun,
        CancellationToken ct)
    {
        if (req.BusinessId == Guid.Empty)
            return BadRequest(new { error = "businessId is required" });

        // Auth: same three-path check used by every other mutation in this controller.
        if (!await IsAuthorizedForBusinessAsync(req.BusinessId, ct))
            return Unauthorized();

        // Role gate: when authenticated via JWT, only Owner / Manager / Founder
        // may reset. Operators can toggle availability but not nuke the menu.
        // Admin-key paths (already accepted above) bypass this — they ARE the operator.
        if (AdminAuth.HasValidJwtRole(User) && !AdminAuth.IsOwnerOrManager(User))
            return Unauthorized(new { error = "Owner, Manager, or Founder role required" });

        // Real (non-dryRun) calls require the typed confirmation string.
        // Server-side check defends against UI bugs / direct API calls.
        if (!dryRun && !string.Equals(req.Confirm, ResetMenuConfirmString, StringComparison.Ordinal))
            return BadRequest(new
            {
                error = $"Confirmation string must be exactly '{ResetMenuConfirmString}'"
            });

        try
        {
            var conn = await GetOpenConnectionAsync(ct);

            // Look up business name once for the response payload (helps the
            // dashboard surface which tenant the counts/deletes apply to).
            string? businessName = null;
            using (var nameCmd = conn.CreateCommand())
            {
                nameCmd.CommandText = """SELECT "Name" FROM "Businesses" WHERE "Id" = @bid LIMIT 1""";
                MakeParam(nameCmd, "bid", req.BusinessId);
                var raw = await nameCmd.ExecuteScalarAsync(ct);
                if (raw is not null and not DBNull) businessName = raw.ToString();
            }

            // ── Counts ──
            // Single round-trip; same SELECTs whether dryRun or real, so the response
            // payload has identical shape and the dashboard renders with one template.
            int categories = 0, items = 0, aliases = 0, extras = 0, upsells = 0;
            using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = """
                    SELECT
                      (SELECT COUNT(*) FROM "MenuCategories" WHERE "BusinessId" = @bid)                                AS categories,
                      (SELECT COUNT(*) FROM "MenuItems" i JOIN "MenuCategories" c ON c."Id" = i."CategoryId"
                          WHERE c."BusinessId" = @bid)                                                                  AS items,
                      (SELECT COUNT(*) FROM "MenuItemAliases" a JOIN "MenuItems" i ON i."Id" = a."MenuItemId"
                          JOIN "MenuCategories" c ON c."Id" = i."CategoryId" WHERE c."BusinessId" = @bid)               AS aliases,
                      (SELECT COUNT(*) FROM "Extras"      WHERE "BusinessId" = @bid)                                    AS extras,
                      (SELECT COUNT(*) FROM "UpsellRules" WHERE "BusinessId" = @bid)                                    AS upsells
                """;
                MakeParam(countCmd, "bid", req.BusinessId);
                using var r = await countCmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    categories = Convert.ToInt32(r["categories"]);
                    items      = Convert.ToInt32(r["items"]);
                    aliases    = Convert.ToInt32(r["aliases"]);
                    extras     = Convert.ToInt32(r["extras"]);
                    upsells    = Convert.ToInt32(r["upsells"]);
                }
            }

            if (dryRun)
            {
                return Ok(new
                {
                    dryRun = true,
                    businessId = req.BusinessId,
                    businessName,
                    deletedCategories = categories,
                    deletedItems      = items,
                    deletedAliases    = aliases,
                    deletedExtras     = extras,
                    deletedUpsells    = upsells,
                    executedAtUtc     = DateTime.UtcNow
                });
            }

            // ── Real wipe — single transaction ──
            // Delete order is defensive against legacy schemas with missing
            // FK cascades, and avoids the UpsellRules.SuggestedMenuItemId
            // ON DELETE SET NULL trap (we want clean removal, not orphan rows).
#pragma warning disable CA2007
            using var tx = await conn.BeginTransactionAsync(ct);
#pragma warning restore CA2007
            try
            {
                // 1. UpsellRules first — by BusinessId, removes both SourceCategoryId
                //    and SuggestedMenuItemId references before either is gone.
                using (var c1 = conn.CreateCommand())
                {
                    c1.Transaction = tx;
                    c1.CommandText = """DELETE FROM "UpsellRules" WHERE "BusinessId" = @bid""";
                    MakeParam(c1, "bid", req.BusinessId);
                    await c1.ExecuteNonQueryAsync(ct);
                }

                // 2a. ExtraMenuItems — explicit, in case the cascade is missing on legacy installs.
                using (var c2a = conn.CreateCommand())
                {
                    c2a.Transaction = tx;
                    c2a.CommandText = """
                        DELETE FROM "ExtraMenuItems"
                        WHERE "ExtraId" IN (SELECT "Id" FROM "Extras" WHERE "BusinessId" = @bid)
                           OR "MenuItemId" IN (
                               SELECT i."Id" FROM "MenuItems" i
                               JOIN "MenuCategories" c ON c."Id" = i."CategoryId"
                               WHERE c."BusinessId" = @bid
                           )
                    """;
                    MakeParam(c2a, "bid", req.BusinessId);
                    await c2a.ExecuteNonQueryAsync(ct);
                }

                // 2b. ExtraMenuCategories — explicit, in case the cascade is missing on legacy installs.
                using (var c2b = conn.CreateCommand())
                {
                    c2b.Transaction = tx;
                    c2b.CommandText = """
                        DELETE FROM "ExtraMenuCategories"
                        WHERE "ExtraId" IN (SELECT "Id" FROM "Extras" WHERE "BusinessId" = @bid)
                           OR "MenuCategoryId" IN (
                               SELECT "Id" FROM "MenuCategories" WHERE "BusinessId" = @bid
                           )
                    """;
                    MakeParam(c2b, "bid", req.BusinessId);
                    await c2b.ExecuteNonQueryAsync(ct);
                }

                // 2c. Extras — cascade would already have removed link rows above; this is the parent row.
                using (var c2 = conn.CreateCommand())
                {
                    c2.Transaction = tx;
                    c2.CommandText = """DELETE FROM "Extras" WHERE "BusinessId" = @bid""";
                    MakeParam(c2, "bid", req.BusinessId);
                    await c2.ExecuteNonQueryAsync(ct);
                }

                // 3. MenuItemAliases — explicit, in case the cascade is missing on legacy installs.
                using (var c3 = conn.CreateCommand())
                {
                    c3.Transaction = tx;
                    c3.CommandText = """
                        DELETE FROM "MenuItemAliases"
                        WHERE "MenuItemId" IN (
                            SELECT i."Id" FROM "MenuItems" i
                            JOIN "MenuCategories" c ON c."Id" = i."CategoryId"
                            WHERE c."BusinessId" = @bid
                        )
                    """;
                    MakeParam(c3, "bid", req.BusinessId);
                    await c3.ExecuteNonQueryAsync(ct);
                }

                // 4. MenuItems — cascade is a safety net for any aliases not caught above.
                using (var c4 = conn.CreateCommand())
                {
                    c4.Transaction = tx;
                    c4.CommandText = """
                        DELETE FROM "MenuItems"
                        WHERE "CategoryId" IN (
                            SELECT "Id" FROM "MenuCategories" WHERE "BusinessId" = @bid
                        )
                    """;
                    MakeParam(c4, "bid", req.BusinessId);
                    await c4.ExecuteNonQueryAsync(ct);
                }

                // 5. MenuCategories.
                using (var c5 = conn.CreateCommand())
                {
                    c5.Transaction = tx;
                    c5.CommandText = """DELETE FROM "MenuCategories" WHERE "BusinessId" = @bid""";
                    MakeParam(c5, "bid", req.BusinessId);
                    await c5.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
            }
            catch
            {
                // Disposal would roll back anyway, but be explicit so failures
                // are visible in the log line below.
                await tx.RollbackAsync(ct);
                throw;
            }

            // Audit log — Warning level so it shows up in any non-trivial Render
            // log filter and is easy to correlate with the matching counts.
            _logger.LogWarning(
                "MENU RESET businessId={BusinessId} businessName={BusinessName} categories={Categories} items={Items} aliases={Aliases} extras={Extras} upsells={Upsells}",
                req.BusinessId, businessName ?? "(unknown)", categories, items, aliases, extras, upsells);

            return Ok(new
            {
                dryRun = false,
                businessId = req.BusinessId,
                businessName,
                deletedCategories = categories,
                deletedItems      = items,
                deletedAliases    = aliases,
                deletedExtras     = extras,
                deletedUpsells    = upsells,
                executedAtUtc     = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin menu endpoint error");
            return StatusCode(500, new { error = "ResetMenu failed: Unexpected server error" });
        }
    }

    private static void ParseTextInput(string raw, List<ParsedCategory> categories,
        List<ParsedExtra> extras, List<ParsedUpsell> upsells, List<string> warnings)
    {
        // section: "categories" (default), "extras", "upsells"
        var section = "categories";
        ParsedCategory? current = null;
        var lines = raw.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // Detect section headers
            if (line.Equals("Extras:", StringComparison.OrdinalIgnoreCase))
            { section = "extras"; current = null; continue; }
            if (line.Equals("Upsells:", StringComparison.OrdinalIgnoreCase))
            { section = "upsells"; current = null; continue; }

            // ── Categories/Products section ──
            if (section == "categories")
            {
                // Detect category line
                if (line.StartsWith("Category:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Categoria:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Categoría:", StringComparison.OrdinalIgnoreCase))
                {
                    var catName = line.Substring(line.IndexOf(':') + 1).Trim();
                    if (string.IsNullOrWhiteSpace(catName))
                    {
                        warnings.Add($"Line {i + 1}: Category with empty name, skipped");
                        current = null;
                        continue;
                    }
                    current = new ParsedCategory { Name = catName };
                    categories.Add(current);
                    continue;
                }

                // Detect item line
                if (line.StartsWith("-") && current != null)
                {
                    var content = line.Substring(1).Trim();
                    var parts = content.Split('|');
                    if (parts.Length < 2)
                    {
                        warnings.Add($"Line {i + 1}: Expected at least name|price, skipped: '{Truncate(content, 50)}'");
                        continue;
                    }

                    var name = parts[0].Trim();
                    var priceStr = parts[1].Trim().Replace(',', '.');
                    if (!decimal.TryParse(priceStr, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var price) || price < 0)
                    {
                        warnings.Add($"Line {i + 1}: Invalid price '{parts[1].Trim()}' for '{name}', skipped");
                        continue;
                    }

                    var desc = parts.Length > 2 ? parts[2].Trim() : null;
                    var aliases = new List<string>();
                    if (parts.Length > 3)
                    {
                        aliases = parts[3].Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
                    }

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        warnings.Add($"Line {i + 1}: Empty item name, skipped");
                        continue;
                    }

                    current.Items.Add(new ParsedItem { Name = name, Price = price, Description = desc, Aliases = aliases });
                    continue;
                }

                if (current == null && !line.StartsWith("-"))
                {
                    warnings.Add($"Line {i + 1}: No category context, skipped: '{Truncate(line, 50)}'");
                }
                continue;
            }

            // ── Extras section ──
            if (section == "extras")
            {
                if (!line.StartsWith("-")) continue;
                var content = line.Substring(1).Trim();
                // Format: name | price | productos: A, B  OR  categorias: X, Y
                var parts = content.Split('|').Select(p => p.Trim()).ToArray();
                if (parts.Length < 1 || string.IsNullOrWhiteSpace(parts[0])) continue;

                var ext = new ParsedExtra { Name = parts[0] };

                if (parts.Length >= 2)
                {
                    var priceStr = parts[1].Replace(',', '.');
                    if (decimal.TryParse(priceStr, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var p) && p >= 0)
                        ext.Price = p;
                }

                // Parse linkage from remaining parts
                for (int j = (parts.Length >= 2 ? 2 : 1); j < parts.Length; j++)
                {
                    var segment = parts[j];
                    if (segment.StartsWith("productos:", StringComparison.OrdinalIgnoreCase))
                    {
                        var names = segment.Substring("productos:".Length).Split(',')
                            .Select(n => n.Trim()).Where(n => n.Length > 0);
                        ext.ProductNames.AddRange(names);
                    }
                    else if (segment.StartsWith("categorias:", StringComparison.OrdinalIgnoreCase) ||
                             segment.StartsWith("categorías:", StringComparison.OrdinalIgnoreCase))
                    {
                        var names = segment.Substring(segment.IndexOf(':') + 1).Split(',')
                            .Select(n => n.Trim()).Where(n => n.Length > 0);
                        ext.CategoryNames.AddRange(names);
                    }
                }

                extras.Add(ext);
                continue;
            }

            // ── Upsells section ──
            if (section == "upsells")
            {
                if (!line.StartsWith("-")) continue;
                var content = line.Substring(1).Trim();
                // Format: categoria origen -> producto sugerido
                var arrowIdx = content.IndexOf("->");
                if (arrowIdx < 0) arrowIdx = content.IndexOf("→");
                if (arrowIdx < 0)
                {
                    warnings.Add($"Line {i + 1}: Upsell needs 'category -> product' format, skipped");
                    continue;
                }
                var srcCat = content.Substring(0, arrowIdx).Trim();
                var sugItem = content.Substring(arrowIdx + (content[arrowIdx] == '→' ? 1 : 2)).Trim();
                if (string.IsNullOrWhiteSpace(srcCat) || string.IsNullOrWhiteSpace(sugItem))
                {
                    warnings.Add($"Line {i + 1}: Upsell missing category or product, skipped");
                    continue;
                }
                upsells.Add(new ParsedUpsell { SourceCategoryName = srcCat, SuggestedItemName = sugItem });
            }
        }
    }

    private static void ParseJsonInput(string raw, List<ParsedCategory> categories, List<string> warnings)
    {
        try
        {
            var arr = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(raw);
            if (arr.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                warnings.Add("JSON root must be an array");
                return;
            }

            foreach (var catEl in arr.EnumerateArray())
            {
                var catName = catEl.TryGetProperty("category", out var cn) ? cn.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(catName))
                {
                    warnings.Add("JSON: category with empty name, skipped");
                    continue;
                }

                var cat = new ParsedCategory { Name = catName };
                if (catEl.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var itemEl in itemsEl.EnumerateArray())
                    {
                        var name = itemEl.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            warnings.Add($"JSON: item with empty name in '{catName}', skipped");
                            continue;
                        }

                        decimal price = 0;
                        if (itemEl.TryGetProperty("price", out var p))
                        {
                            if (p.ValueKind == System.Text.Json.JsonValueKind.Number)
                                price = p.GetDecimal();
                            else if (p.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                var ps = p.GetString()?.Replace(',', '.') ?? "0";
                                decimal.TryParse(ps, System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture, out price);
                            }
                        }

                        var desc = itemEl.TryGetProperty("description", out var d) ? d.GetString() : null;

                        var aliases = new List<string>();
                        if (itemEl.TryGetProperty("aliases", out var al) && al.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var a in al.EnumerateArray())
                            {
                                var av = a.GetString();
                                if (!string.IsNullOrWhiteSpace(av)) aliases.Add(av);
                            }
                        }

                        cat.Items.Add(new ParsedItem { Name = name, Price = price, Description = desc, Aliases = aliases });
                    }
                }
                categories.Add(cat);
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            warnings.Add($"Invalid JSON: {ex.Message}");
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";

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
            _logger.LogError(ex, "Admin menu endpoint error");
            return StatusCode(500, new { error = $"PublicMenu failed: Unexpected server error" });
        }
    }
}
