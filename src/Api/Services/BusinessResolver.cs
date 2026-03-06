using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Serilog;
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

        // Use raw ADO.NET to avoid EF's NpgsqlBoolOptimizingExpressionVisitor
        // which generates `WHERE "IsActive"` (bare boolean predicate) that fails
        // if the column is INTEGER in Postgres.
        var conn = _db.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "Id", "PhoneNumberId", "AccessToken"
            FROM "Businesses"
            WHERE "PhoneNumberId" = @pid AND CAST("IsActive" AS integer) = 1
            LIMIT 1
            """;

        var param = cmd.CreateParameter();
        param.ParameterName = "@pid";
        param.Value = id;
        cmd.Parameters.Add(param);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var bizId = reader.GetGuid(0);
        var bizPhone = reader.GetString(1);
        var bizToken = reader.GetString(2);

        return new BusinessContext(bizId, bizPhone, bizToken);
    }
}
