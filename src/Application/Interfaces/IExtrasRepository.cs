using WhatsAppSaaS.Application.Models;

namespace WhatsAppSaaS.Application.Interfaces;

public interface IExtrasRepository
{
    /// <summary>
    /// Returns deduplicated active extras for a menu item, combining product-level
    /// and category-level linkages. Product-level takes precedence for AppliesVia.
    /// Ordered by SortOrder asc, then Name asc.
    /// </summary>
    Task<List<ExtraReadModel>> GetExtrasForItemAsync(
        Guid businessId, Guid menuItemId, Guid categoryId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns active upsell rules for a source category within a business.
    /// Includes suggested MenuItem name/price when linked.
    /// Ordered by SortOrder asc, then SuggestionLabel asc.
    /// </summary>
    Task<List<UpsellReadModel>> GetUpsellsForCategoryAsync(
        Guid businessId, Guid sourceCategoryId,
        CancellationToken ct = default);
}
