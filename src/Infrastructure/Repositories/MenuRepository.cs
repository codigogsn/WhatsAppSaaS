using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Infrastructure.Repositories;

public sealed class MenuRepository : IMenuRepository
{
    private readonly AppDbContext _db;

    public MenuRepository(AppDbContext db) => _db = db;

    public async Task<List<MenuCategory>> GetCategoriesAsync(Guid businessId, CancellationToken ct = default)
    {
        return await _db.MenuCategories
            .AsNoTracking()
            .Where(c => c.BusinessId == businessId && c.IsActive)
            .Include(c => c.Items.Where(i => i.IsAvailable))
                .ThenInclude(i => i.Aliases)
            .OrderBy(c => c.SortOrder)
            .ToListAsync(ct);
    }

    public async Task<List<MenuItem>> GetAvailableItemsAsync(Guid businessId, CancellationToken ct = default)
    {
        return await _db.MenuItems
            .AsNoTracking()
            .Where(i => i.IsAvailable && i.Category!.BusinessId == businessId && i.Category.IsActive)
            .Include(i => i.Aliases)
            .OrderBy(i => i.SortOrder)
            .ToListAsync(ct);
    }
}
