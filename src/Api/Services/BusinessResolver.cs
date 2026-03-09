using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace Api.Services;

public interface IBusinessResolver
{
    Task<BusinessContext?> ResolveByPhoneNumberIdAsync(string? phoneNumberId, CancellationToken ct = default);
    Task<BusinessContext?> ResolveOrCreateAsync(string? phoneNumberId, CancellationToken ct = default);
}

public class BusinessResolver : IBusinessResolver
{
    private readonly AppDbContext _db;
    private readonly string? _menuPdfUrl;

    public BusinessResolver(AppDbContext db, IOptions<WhatsAppOptions>? whatsAppOptions = null)
    {
        _db = db;

        // Build menu PDF URL: config > PUBLIC_BASE_URL > RENDER_EXTERNAL_URL (auto-provided by Render)
        var configBase = whatsAppOptions?.Value?.PublicBaseUrl;
        var envBase = Environment.GetEnvironmentVariable("PUBLIC_BASE_URL");
        var renderUrl = Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL");
        var baseUrl = configBase ?? envBase ?? renderUrl;
        Log.Information("MENU PDF CONFIG — PublicBaseUrl(config)={ConfigBase} PUBLIC_BASE_URL(env)={EnvBase} RENDER_EXTERNAL_URL={RenderUrl} resolved={Resolved}",
            configBase ?? "(null)", envBase ?? "(null)", renderUrl ?? "(null)", baseUrl ?? "(null)");
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            _menuPdfUrl = baseUrl.TrimEnd('/') + "/menu-demo.pdf";
            Log.Information("MENU PDF URL resolved: {MenuPdfUrl}", _menuPdfUrl);
        }
        else
        {
            Log.Error("MENU PDF URL NOT CONFIGURED — set PUBLIC_BASE_URL env var, WhatsApp:PublicBaseUrl in appsettings, or deploy on Render (auto-detects RENDER_EXTERNAL_URL). " +
                       "The bot will NOT be able to send the PDF menu.");
        }
    }

    public async Task<BusinessContext?> ResolveByPhoneNumberIdAsync(string? phoneNumberId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(phoneNumberId))
            return null;

        var id = phoneNumberId.Trim();
        return await FindByPhoneNumberIdAsync(id, ct);
    }

    public async Task<BusinessContext?> ResolveOrCreateAsync(string? phoneNumberId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(phoneNumberId))
            return null;

        var id = phoneNumberId.Trim();

        var existing = await FindByPhoneNumberIdAsync(id, ct);
        if (existing is not null)
        {
            Log.Information("BUSINESS RESOLVED phoneNumberId={PhoneNumberId} businessId={BusinessId}", id, existing.BusinessId);
            return existing;
        }

        // Auto-create: resolve config from env vars
        var accessToken = EnvResolve("WHATSAPP_ACCESS_TOKEN", "WhatsApp__AccessToken") ?? "";
        var adminKey = EnvResolve("WHATSAPP_ADMIN_KEY", "ADMIN_KEY", "WhatsApp__AdminKey") ?? "";
        var bizName = EnvResolve("WHATSAPP_BUSINESS_NAME", "WhatsApp__BusinessName") ?? "Default Business";

        var biz = new Business
        {
            Name = bizName,
            PhoneNumberId = id,
            AccessToken = accessToken,
            AdminKey = adminKey,
            IsActive = true
        };

        _db.Businesses.Add(biz);
        try
        {
            await _db.SaveChangesAsync(ct);
            Log.Information("BUSINESS AUTO-CREATED phoneNumberId={PhoneNumberId} businessId={BusinessId} name={Name}",
                id, biz.Id, bizName);
        }
        catch (DbUpdateException)
        {
            // Unique constraint violation — another request created it concurrently
            _db.Entry(biz).State = EntityState.Detached;
            var retry = await FindByPhoneNumberIdAsync(id, ct);
            if (retry is not null)
            {
                Log.Information("BUSINESS RESOLVED (concurrent) phoneNumberId={PhoneNumberId} businessId={BusinessId}", id, retry.BusinessId);
                return retry;
            }
            Log.Warning("BUSINESS AUTO-CREATE failed and lookup also failed for phoneNumberId={PhoneNumberId}", id);
            return null;
        }

        return new BusinessContext(biz.Id, biz.PhoneNumberId, biz.AccessToken, biz.Name,
            biz.Greeting, biz.Schedule, biz.Address, biz.LogoUrl,
            biz.PaymentMobileBank, biz.PaymentMobileId, biz.PaymentMobilePhone, biz.NotificationPhone,
            MenuPdfUrl: _menuPdfUrl);
    }

    private async Task<BusinessContext?> FindByPhoneNumberIdAsync(string id, CancellationToken ct)
    {
        // Use raw ADO.NET to avoid EF's NpgsqlBoolOptimizingExpressionVisitor
        // which generates bare boolean predicates that fail on INTEGER columns.
        var conn = _db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "Id", "PhoneNumberId", "AccessToken", "Name",
                   "PaymentMobileBank", "PaymentMobileId", "PaymentMobilePhone",
                   "Greeting", "Schedule", "Address", "LogoUrl", "NotificationPhone",
                   "RestaurantType"
            FROM "Businesses"
            WHERE "PhoneNumberId" = @pid AND "IsActive" = true
            LIMIT 1
            """;

        var param = cmd.CreateParameter();
        param.ParameterName = "@pid";
        param.Value = id;
        cmd.Parameters.Add(param);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        // Read Id as string to support both TEXT and UUID column types in legacy/new schemas
        var rawId = reader.GetValue(0)?.ToString() ?? "";
        if (!Guid.TryParse(rawId, out var bizId))
        {
            Log.Warning("BUSINESS RESOLVE: invalid Id format for phoneNumberId={PhoneNumberId}, rawId={RawId}", id, rawId);
            return null;
        }
        var bizPhone = reader.GetString(1);
        var bizToken = reader.GetString(2);
        var bizName = reader.IsDBNull(3) ? "" : reader.GetString(3);
        var pmBank = reader.IsDBNull(4) ? null : reader.GetString(4);
        var pmId = reader.IsDBNull(5) ? null : reader.GetString(5);
        var pmPhone = reader.IsDBNull(6) ? null : reader.GetString(6);
        var greeting = reader.IsDBNull(7) ? null : reader.GetString(7);
        var schedule = reader.IsDBNull(8) ? null : reader.GetString(8);
        var address = reader.IsDBNull(9) ? null : reader.GetString(9);
        var logoUrl = reader.IsDBNull(10) ? null : reader.GetString(10);
        var notificationPhone = reader.IsDBNull(11) ? null : reader.GetString(11);
        var restaurantType = reader.IsDBNull(12) ? null : reader.GetString(12);

        return new BusinessContext(bizId, bizPhone, bizToken, bizName,
            greeting, schedule, address, logoUrl, pmBank, pmId, pmPhone, notificationPhone, restaurantType,
            _menuPdfUrl);
    }

    public static string? EnvResolve(params string[] keys)
    {
        foreach (var key in keys)
        {
            var val = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(val))
                return val;
        }
        return null;
    }
}
