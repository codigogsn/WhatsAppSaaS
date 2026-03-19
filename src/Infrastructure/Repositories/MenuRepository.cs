using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Infrastructure.Repositories;

/// <summary>
/// Uses raw ADO.NET because production MenuCategories.IsActive and
/// MenuItems.IsAvailable are integer columns, not boolean.
/// EF would throw "column X is of type integer but expression is of type boolean".
/// </summary>
public sealed class MenuRepository : IMenuRepository
{
    private readonly AppDbContext _db;

    /// <summary>
    /// Safely reads a decimal value from a DB reader, handling text-typed columns
    /// (from SQLite-generated migrations) by parsing the string value.
    /// </summary>
    private static decimal SafeGetDecimal(System.Data.Common.DbDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal)) return 0m;
        try { return r.GetDecimal(ordinal); }
        catch (InvalidCastException)
        {
            var raw = r.GetValue(ordinal)?.ToString();
            return decimal.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var val) ? val : 0m;
        }
    }

    /// <summary>
    /// Safely reads a DateTime value from a DB reader, handling text-typed columns
    /// (from SQLite-generated migrations) by parsing the string value.
    /// </summary>
    private static DateTime SafeGetDateTime(System.Data.Common.DbDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal)) return DateTime.UtcNow;
        try { return r.GetDateTime(ordinal); }
        catch (InvalidCastException)
        {
            var raw = r.GetValue(ordinal)?.ToString();
            return DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var val) ? val : DateTime.UtcNow;
        }
    }

    public MenuRepository(AppDbContext db) => _db = db;

    private static bool ParseBool(object raw) => raw switch
    {
        bool b => b,
        int i => i != 0,
        long l => l != 0,
        string s => s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase),
        DBNull => true,
        _ => true
    };

    public async Task<List<MenuCategory>> GetCategoriesAsync(Guid businessId, CancellationToken ct = default)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        // Load active categories
        using var catCmd = conn.CreateCommand();
        catCmd.CommandText = """
            SELECT "Id", "BusinessId", "Name", "SortOrder", "IsActive", "CreatedAtUtc"
            FROM "MenuCategories"
            WHERE "BusinessId" = @bid AND "IsActive"::boolean = true
            ORDER BY "SortOrder"
        """;
        var p = catCmd.CreateParameter();
        p.ParameterName = "bid";
        p.Value = businessId;
        catCmd.Parameters.Add(p);

        var categories = new List<MenuCategory>();
        var catIds = new List<Guid>();
        using var cr = await catCmd.ExecuteReaderAsync(ct);
        while (await cr.ReadAsync(ct))
        {
            var cat = new MenuCategory
            {
                Id = cr.GetGuid(cr.GetOrdinal("Id")),
                BusinessId = cr.GetGuid(cr.GetOrdinal("BusinessId")),
                Name = cr.GetString(cr.GetOrdinal("Name")),
                SortOrder = cr.GetInt32(cr.GetOrdinal("SortOrder")),
                IsActive = ParseBool(cr["IsActive"]),
                CreatedAtUtc = SafeGetDateTime(cr, cr.GetOrdinal("CreatedAtUtc")),
                Items = new List<MenuItem>()
            };
            categories.Add(cat);
            catIds.Add(cat.Id);
        }
        await cr.CloseAsync();

        if (catIds.Count == 0)
            return categories;

        // Load available items for those categories
        using var itemCmd = conn.CreateCommand();
        var paramNames = new List<string>();
        for (var i = 0; i < catIds.Count; i++)
        {
            var pn = $"@c{i}";
            paramNames.Add(pn);
            var ip = itemCmd.CreateParameter();
            ip.ParameterName = $"c{i}";
            ip.Value = catIds[i];
            itemCmd.Parameters.Add(ip);
        }
        itemCmd.CommandText = $"""
            SELECT "Id", "CategoryId", "Name", "Price", "Description", "IsAvailable", "SortOrder", "CreatedAtUtc"
            FROM "MenuItems"
            WHERE "CategoryId" IN ({string.Join(",", paramNames)})
              AND "IsAvailable"::boolean = true
            ORDER BY "SortOrder"
        """;

        var itemsById = new Dictionary<Guid, MenuItem>();
        var itemCategoryMap = new Dictionary<Guid, Guid>();
        using var ir = await itemCmd.ExecuteReaderAsync(ct);
        while (await ir.ReadAsync(ct))
        {
            var descOrd = ir.GetOrdinal("Description");
            var item = new MenuItem
            {
                Id = ir.GetGuid(ir.GetOrdinal("Id")),
                CategoryId = ir.GetGuid(ir.GetOrdinal("CategoryId")),
                Name = ir.GetString(ir.GetOrdinal("Name")),
                Price = SafeGetDecimal(ir, ir.GetOrdinal("Price")),
                Description = ir.IsDBNull(descOrd) ? null : ir.GetString(descOrd),
                IsAvailable = ParseBool(ir["IsAvailable"]),
                SortOrder = ir.GetInt32(ir.GetOrdinal("SortOrder")),
                CreatedAtUtc = SafeGetDateTime(ir, ir.GetOrdinal("CreatedAtUtc")),
                Aliases = new List<MenuItemAlias>()
            };
            itemsById[item.Id] = item;
            itemCategoryMap[item.Id] = item.CategoryId;
        }
        await ir.CloseAsync();

        // Load aliases for those items
        if (itemsById.Count > 0)
        {
            using var aliasCmd = conn.CreateCommand();
            var aliasParamNames = new List<string>();
            var idx = 0;
            foreach (var itemId in itemsById.Keys)
            {
                var pn = $"@i{idx}";
                aliasParamNames.Add(pn);
                var ap = aliasCmd.CreateParameter();
                ap.ParameterName = $"i{idx}";
                ap.Value = itemId;
                aliasCmd.Parameters.Add(ap);
                idx++;
            }
            aliasCmd.CommandText = $"""
                SELECT "Id", "MenuItemId", "Alias"
                FROM "MenuItemAliases"
                WHERE "MenuItemId" IN ({string.Join(",", aliasParamNames)})
            """;
            using var ar = await aliasCmd.ExecuteReaderAsync(ct);
            while (await ar.ReadAsync(ct))
            {
                var alias = new MenuItemAlias
                {
                    Id = ar.GetGuid(ar.GetOrdinal("Id")),
                    MenuItemId = ar.GetGuid(ar.GetOrdinal("MenuItemId")),
                    Alias = ar.GetString(ar.GetOrdinal("Alias"))
                };
                if (itemsById.TryGetValue(alias.MenuItemId, out var parentItem))
                    parentItem.Aliases.Add(alias);
            }
        }

        // Assign items to categories
        var catMap = categories.ToDictionary(c => c.Id);
        foreach (var item in itemsById.Values)
        {
            if (catMap.TryGetValue(item.CategoryId, out var cat))
                cat.Items.Add(item);
        }

        return categories;
    }

    public async Task<List<MenuItem>> GetAvailableItemsAsync(Guid businessId, CancellationToken ct = default)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i."Id", i."CategoryId", i."Name", i."Price", i."Description",
                   i."IsAvailable", i."SortOrder", i."CreatedAtUtc"
            FROM "MenuItems" i
            JOIN "MenuCategories" c ON c."Id" = i."CategoryId"
            WHERE c."BusinessId" = @bid
              AND c."IsActive"::boolean = true
              AND i."IsAvailable"::boolean = true
            ORDER BY i."SortOrder"
        """;
        var p = cmd.CreateParameter();
        p.ParameterName = "bid";
        p.Value = businessId;
        cmd.Parameters.Add(p);

        var items = new List<MenuItem>();
        var itemIds = new List<Guid>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var descOrd = reader.GetOrdinal("Description");
            var item = new MenuItem
            {
                Id = reader.GetGuid(reader.GetOrdinal("Id")),
                CategoryId = reader.GetGuid(reader.GetOrdinal("CategoryId")),
                Name = reader.GetString(reader.GetOrdinal("Name")),
                Price = SafeGetDecimal(reader, reader.GetOrdinal("Price")),
                Description = reader.IsDBNull(descOrd) ? null : reader.GetString(descOrd),
                IsAvailable = ParseBool(reader["IsAvailable"]),
                SortOrder = reader.GetInt32(reader.GetOrdinal("SortOrder")),
                CreatedAtUtc = SafeGetDateTime(reader, reader.GetOrdinal("CreatedAtUtc")),
                Aliases = new List<MenuItemAlias>()
            };
            items.Add(item);
            itemIds.Add(item.Id);
        }
        await reader.CloseAsync();

        // Load aliases
        if (itemIds.Count > 0)
        {
            using var aliasCmd = conn.CreateCommand();
            var paramNames = new List<string>();
            for (var i = 0; i < itemIds.Count; i++)
            {
                var pn = $"@i{i}";
                paramNames.Add(pn);
                var ap = aliasCmd.CreateParameter();
                ap.ParameterName = $"i{i}";
                ap.Value = itemIds[i];
                aliasCmd.Parameters.Add(ap);
            }
            aliasCmd.CommandText = $"""
                SELECT "Id", "MenuItemId", "Alias"
                FROM "MenuItemAliases"
                WHERE "MenuItemId" IN ({string.Join(",", paramNames)})
            """;
            using var ar = await aliasCmd.ExecuteReaderAsync(ct);
            while (await ar.ReadAsync(ct))
            {
                var alias = new MenuItemAlias
                {
                    Id = ar.GetGuid(ar.GetOrdinal("Id")),
                    MenuItemId = ar.GetGuid(ar.GetOrdinal("MenuItemId")),
                    Alias = ar.GetString(ar.GetOrdinal("Alias"))
                };
                var parentItem = items.FirstOrDefault(x => x.Id == alias.MenuItemId);
                parentItem?.Aliases.Add(alias);
            }
        }

        return items;
    }
}
