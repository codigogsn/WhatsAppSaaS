using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace Api.Services;

public interface IBusinessResolver
{
    Task<BusinessContext?> ResolveByPhoneNumberIdAsync(string? phoneNumberId, CancellationToken ct = default);
}

public class BusinessResolver : IBusinessResolver
{
    private readonly AppDbContext _db;

    public BusinessResolver(AppDbContext db)
    {
        _db = db;
    }

    public async Task<BusinessContext?> ResolveByPhoneNumberIdAsync(string? phoneNumberId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(phoneNumberId))
            return null;

        var id = phoneNumberId.Trim();

        var biz = await _db.Businesses
            .AsNoTracking()
            .Where(b => b.IsActive && b.PhoneNumberId == id)
            .Select(b => new { b.Id, b.PhoneNumberId, b.AccessToken })
            .FirstOrDefaultAsync(ct);

        return biz is null ? null : new BusinessContext(biz.Id, biz.PhoneNumberId, biz.AccessToken);
    }
}
