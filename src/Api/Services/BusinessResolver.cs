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

        var currRef = EnvResolve("WHATSAPP_CURRENCY_REF", "WhatsApp__CurrencyReference") ?? "BCV_USD";

        var biz = new Business
        {
            Name = bizName,
            PhoneNumberId = id,
            AccessToken = accessToken,
            AdminKey = adminKey,
            IsActive = true,
            CurrencyReference = currRef
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
        // SELECT * to automatically pick up new columns (e.g. Zelle fields)
        cmd.CommandText = """
            SELECT * FROM "Businesses"
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

        // Build column lookup for safe access (handles missing columns in legacy schemas)
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++) cols.Add(reader.GetName(i));
        string? Col(string name) => cols.Contains(name) && reader[name] is not DBNull ? reader[name]?.ToString() : null;

        var rawId = Col("Id") ?? "";
        if (!Guid.TryParse(rawId, out var bizId))
        {
            Log.Warning("BUSINESS RESOLVE: invalid Id format for phoneNumberId={PhoneNumberId}, rawId={RawId}", id, rawId);
            return null;
        }

        return new BusinessContext(bizId,
            Col("PhoneNumberId") ?? id,
            Col("AccessToken") ?? "",
            Col("Name") ?? "",
            Col("Greeting"), Col("Schedule"), Col("Address"), Col("LogoUrl"),
            Col("PaymentMobileBank"), Col("PaymentMobileId"), Col("PaymentMobilePhone"),
            Col("NotificationPhone"), Col("RestaurantType"),
            Col("MenuPdfUrl") ?? _menuPdfUrl,
            Col("CurrencyReference"),
            Col("VerticalType") ?? "restaurant",
            Col("ZelleRecipient"), Col("ZelleInstructions"));
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
