using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Application.Interfaces;

public interface IMenuRepository
{
    Task<List<MenuCategory>> GetCategoriesAsync(Guid businessId, CancellationToken ct = default);
    Task<List<MenuItem>> GetAvailableItemsAsync(Guid businessId, CancellationToken ct = default);
}
