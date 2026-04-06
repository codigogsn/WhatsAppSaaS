using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Application.Models;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Infrastructure.Repositories;

public sealed class ExtrasRepository : IExtrasRepository
{
    private readonly AppDbContext _db;

    public ExtrasRepository(AppDbContext db) => _db = db;

    public async Task<List<ExtraReadModel>> GetExtrasForItemAsync(
        Guid businessId, Guid menuItemId, Guid categoryId,
        CancellationToken ct = default)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        // Single query: union product-level and category-level extras,
        // deduplicate preferring product-level via DISTINCT ON.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "Id", "Name", "AdditivePrice", "AppliesVia", "SortOrder"
            FROM (
                SELECT DISTINCT ON (e."Id")
                    e."Id", e."Name", e."AdditivePrice",
                    CASE WHEN emi."MenuItemId" IS NOT NULL THEN 'Product' ELSE 'Category' END AS "AppliesVia",
                    e."SortOrder"
                FROM "Extras" e
                LEFT JOIN "ExtraMenuItems" emi
                    ON emi."ExtraId" = e."Id" AND emi."MenuItemId" = @itemId
                LEFT JOIN "ExtraMenuCategories" emc
                    ON emc."ExtraId" = e."Id" AND emc."MenuCategoryId" = @catId
                WHERE e."BusinessId" = @bid
                  AND e."IsActive" = true
                  AND (emi."MenuItemId" IS NOT NULL OR emc."MenuCategoryId" IS NOT NULL)
                ORDER BY e."Id",
                    CASE WHEN emi."MenuItemId" IS NOT NULL THEN 0 ELSE 1 END
            ) sub
            ORDER BY "SortOrder", "Name"
            """;

        AddParam(cmd, "bid", businessId);
        AddParam(cmd, "itemId", menuItemId);
        AddParam(cmd, "catId", categoryId);

        var results = new List<ExtraReadModel>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            results.Add(new ExtraReadModel
            {
                Id = r.GetGuid(r.GetOrdinal("Id")),
                Name = r.GetString(r.GetOrdinal("Name")),
                AdditivePrice = r.IsDBNull(r.GetOrdinal("AdditivePrice"))
                    ? null : r.GetDecimal(r.GetOrdinal("AdditivePrice")),
                AppliesVia = r.GetString(r.GetOrdinal("AppliesVia")),
                SortOrder = r.GetInt32(r.GetOrdinal("SortOrder"))
            });
        }
        return results;
    }

    public async Task<List<UpsellReadModel>> GetUpsellsForCategoryAsync(
        Guid businessId, Guid sourceCategoryId,
        CancellationToken ct = default)
    {
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT u."Id", u."SuggestedMenuItemId", u."SuggestionLabel",
                   u."CustomMessage", u."SortOrder",
                   mi."Name" AS "ItemName", mi."Price" AS "ItemPrice"
            FROM "UpsellRules" u
            LEFT JOIN "MenuItems" mi ON mi."Id" = u."SuggestedMenuItemId"
            WHERE u."BusinessId" = @bid
              AND u."SourceCategoryId" = @catId
              AND u."IsActive" = true
            ORDER BY u."SortOrder", u."SuggestionLabel"
            """;

        AddParam(cmd, "bid", businessId);
        AddParam(cmd, "catId", sourceCategoryId);

        var results = new List<UpsellReadModel>();
        using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var itemNameOrd = r.GetOrdinal("ItemName");
            var itemPriceOrd = r.GetOrdinal("ItemPrice");
            var suggestedIdOrd = r.GetOrdinal("SuggestedMenuItemId");

            results.Add(new UpsellReadModel
            {
                Id = r.GetGuid(r.GetOrdinal("Id")),
                SuggestedMenuItemId = r.IsDBNull(suggestedIdOrd) ? null : r.GetGuid(suggestedIdOrd),
                SuggestedMenuItemName = r.IsDBNull(itemNameOrd) ? null : r.GetString(itemNameOrd),
                SuggestedMenuItemPrice = r.IsDBNull(itemPriceOrd) ? null : r.GetDecimal(itemPriceOrd),
                SuggestionLabel = r.IsDBNull(r.GetOrdinal("SuggestionLabel"))
                    ? null : r.GetString(r.GetOrdinal("SuggestionLabel")),
                CustomMessage = r.IsDBNull(r.GetOrdinal("CustomMessage"))
                    ? null : r.GetString(r.GetOrdinal("CustomMessage")),
                SortOrder = r.GetInt32(r.GetOrdinal("SortOrder"))
            });
        }
        return results;
    }

    private static void AddParam(System.Data.Common.DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}
