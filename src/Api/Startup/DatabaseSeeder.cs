using Microsoft.EntityFrameworkCore;
using Serilog;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Startup;

/// <summary>
/// Startup data seeds: default business, Zelle config, founder account.
/// All seeds are idempotent and use raw SQL to avoid EF type-mismatch issues.
/// </summary>
public static class DatabaseSeeder
{
    public static void RunAll(IServiceProvider services)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SeedDefaultBusiness(services);
        SeedZelleConfig(services);
        SeedFounderOwner(services);
        Log.Information("STARTUP SEEDS completed in {Elapsed}ms", sw.ElapsedMilliseconds);
    }

    private static void SeedDefaultBusiness(IServiceProvider services)
    {
        var phoneNumberId = Environment.GetEnvironmentVariable("WHATSAPP_PHONE_NUMBER_ID");
        if (string.IsNullOrWhiteSpace(phoneNumberId))
        {
            Log.Information("SEED: WHATSAPP_PHONE_NUMBER_ID not set, skipping business seed");
            return;
        }

        var accessToken = Environment.GetEnvironmentVariable("WHATSAPP_ACCESS_TOKEN") ?? "";
        var adminKey = Environment.GetEnvironmentVariable("WHATSAPP_ADMIN_KEY") ?? "";
        var businessName = Environment.GetEnvironmentVariable("WHATSAPP_BUSINESS_NAME") ?? "Default Business";

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) conn.Open();

            // Legacy non-UUID business IDs are now cleaned up by SchemaRepair.CleanupInvalidBusinessIds

            string? existingId = null;
            string? existingToken = null;
            string? existingAdminKey = null;
            bool existingActive = false;

            using (var readCmd = conn.CreateCommand())
            {
                readCmd.CommandText = """
                    SELECT CAST("Id" AS TEXT), "AccessToken", "AdminKey", "IsActive"
                    FROM "Businesses"
                    WHERE "PhoneNumberId" = @pid
                    LIMIT 1
                """;
                var p = readCmd.CreateParameter(); p.ParameterName = "pid"; p.Value = phoneNumberId; readCmd.Parameters.Add(p);

                using var r = readCmd.ExecuteReader();
                if (r.Read())
                {
                    existingId = r.IsDBNull(0) ? null : r.GetString(0);
                    existingToken = r.IsDBNull(1) ? null : r.GetString(1);
                    existingAdminKey = r.IsDBNull(2) ? null : r.GetString(2);
                    var rawActive = r["IsActive"];
                    existingActive = rawActive switch
                    {
                        bool bv => bv, int iv => iv != 0, long lv => lv != 0,
                        string sv => sv is "1" or "true" or "True" or "t", _ => true
                    };
                }
            }

            if (existingId is not null)
            {
                var sets = new List<string>();
                var updateParams = new List<(string name, object value)>();

                if (!string.IsNullOrWhiteSpace(accessToken) && accessToken != existingToken)
                {
                    sets.Add("\"AccessToken\" = @token");
                    updateParams.Add(("token", accessToken));
                }
                if (!string.IsNullOrWhiteSpace(adminKey) && adminKey != existingAdminKey)
                {
                    sets.Add("\"AdminKey\" = @akey");
                    updateParams.Add(("akey", adminKey));
                }
                if (!existingActive)
                {
                    sets.Add("\"IsActive\" = true");
                }

                if (sets.Count > 0)
                {
                    using var updCmd = conn.CreateCommand();
                    updCmd.CommandText = $"""UPDATE "Businesses" SET {string.Join(", ", sets)} WHERE "PhoneNumberId" = @pid""";
                    var pp = updCmd.CreateParameter(); pp.ParameterName = "pid"; pp.Value = phoneNumberId; updCmd.Parameters.Add(pp);
                    foreach (var (name, value) in updateParams)
                    {
                        var up = updCmd.CreateParameter(); up.ParameterName = name; up.Value = value; updCmd.Parameters.Add(up);
                    }
                    updCmd.ExecuteNonQuery();
                    Log.Information("SEED: updated existing business PhoneNumberId={PhoneNumberId} Id={Id}", phoneNumberId, existingId);
                }
                else
                {
                    Log.Information("SEED: business already exists PhoneNumberId={PhoneNumberId} Id={Id} IsActive={IsActive}",
                        phoneNumberId, existingId, existingActive);
                }
                return;
            }

            var newId = Guid.NewGuid();
            using (var insCmd = conn.CreateCommand())
            {
                insCmd.CommandText = """
                    INSERT INTO "Businesses" ("Id", "Name", "PhoneNumberId", "AccessToken", "AdminKey", "IsActive", "CreatedAtUtc")
                    VALUES (@id, @name, @pid, @token, @akey, true, @created)
                """;
                var ip1 = insCmd.CreateParameter(); ip1.ParameterName = "id"; ip1.Value = newId; insCmd.Parameters.Add(ip1);
                var ip2 = insCmd.CreateParameter(); ip2.ParameterName = "name"; ip2.Value = businessName; insCmd.Parameters.Add(ip2);
                var ip3 = insCmd.CreateParameter(); ip3.ParameterName = "pid"; ip3.Value = phoneNumberId; insCmd.Parameters.Add(ip3);
                var ip4 = insCmd.CreateParameter(); ip4.ParameterName = "token"; ip4.Value = accessToken; insCmd.Parameters.Add(ip4);
                var ip5 = insCmd.CreateParameter(); ip5.ParameterName = "akey"; ip5.Value = adminKey; insCmd.Parameters.Add(ip5);
                var ip6 = insCmd.CreateParameter(); ip6.ParameterName = "created"; ip6.Value = DateTime.UtcNow; insCmd.Parameters.Add(ip6);
                insCmd.ExecuteNonQuery();
            }
            Log.Information("SEED: created business PhoneNumberId={PhoneNumberId} Id={Id} Name={Name}", phoneNumberId, newId, businessName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SEED: failed to seed business for PhoneNumberId={PhoneNumberId}", phoneNumberId);
        }
    }

    private static void SeedZelleConfig(IServiceProvider services)
    {
        var zelleRecipient = Environment.GetEnvironmentVariable("ZELLE_RECIPIENT")?.Trim();
        var zelleInstructions = Environment.GetEnvironmentVariable("ZELLE_INSTRUCTIONS")?.Trim();

        if (string.IsNullOrWhiteSpace(zelleRecipient))
        {
            Log.Information("SEED ZELLE: skipped — ZELLE_RECIPIENT env var not set (will not inject default payment data)");
            return;
        }

        if (string.IsNullOrWhiteSpace(zelleInstructions))
            zelleInstructions = "Enviar comprobante por WhatsApp";

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE "Businesses"
                SET "ZelleRecipient" = @recip, "ZelleInstructions" = @instr
                WHERE CAST("IsActive" AS TEXT) IN ('true','1','t') AND "ZelleRecipient" IS NULL
            """;
            var p1 = cmd.CreateParameter(); p1.ParameterName = "recip"; p1.Value = zelleRecipient; cmd.Parameters.Add(p1);
            var p2 = cmd.CreateParameter(); p2.ParameterName = "instr"; p2.Value = zelleInstructions; cmd.Parameters.Add(p2);
            var rows = cmd.ExecuteNonQuery();
            Log.Information("SEED ZELLE: updated {Rows} business(es) with ZelleRecipient/ZelleInstructions from env vars", rows);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "SEED ZELLE: failed to seed Zelle config");
        }
    }

    private static void SeedFounderOwner(IServiceProvider services)
    {
        Log.Information("FOUNDER SEED: started");

        var rawEmail = Environment.GetEnvironmentVariable("FOUNDER_EMAIL");
        var rawPassword = Environment.GetEnvironmentVariable("FOUNDER_PASSWORD");
        var founderName = Environment.GetEnvironmentVariable("FOUNDER_NAME") ?? "Founder";

        Log.Information("FOUNDER SEED: normalized email = {Email}", rawEmail?.Trim().ToLowerInvariant() ?? "(null)");
        Log.Information("FOUNDER SEED: password present = {HasPw}", !string.IsNullOrWhiteSpace(rawPassword));

        if (string.IsNullOrWhiteSpace(rawEmail))
        {
            Log.Information("FOUNDER SEED: FOUNDER_EMAIL not set, skipping");
            Log.Information("FOUNDER SEED: finished");
            return;
        }
        if (string.IsNullOrWhiteSpace(rawPassword))
        {
            Log.Warning("FOUNDER SEED: FOUNDER_PASSWORD not set, skipping");
            Log.Information("FOUNDER SEED: finished");
            return;
        }

        var founderEmail = rawEmail.Trim().ToLowerInvariant();

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        try
        {
            var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open) conn.Open();

            bool tableExists;
            using (var tblCmd = conn.CreateCommand())
            {
                tblCmd.CommandText = "SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name='BusinessUsers'";
                tableExists = tblCmd.ExecuteScalar() is not null;
            }
            Log.Information("FOUNDER SEED: BusinessUsers table exists = {Exists}", tableExists);
            if (!tableExists)
            {
                Log.Error("FOUNDER SEED: cannot proceed without BusinessUsers table");
                Log.Information("FOUNDER SEED: finished");
                return;
            }

            long currentRowCount = 0;
            using (var cntCmd = conn.CreateCommand())
            {
                cntCmd.CommandText = """SELECT COUNT(*) FROM "BusinessUsers" """;
                currentRowCount = Convert.ToInt64(cntCmd.ExecuteScalar());
            }
            Log.Information("FOUNDER SEED: current BusinessUsers row count = {Count}", currentRowCount);

            Log.Information("FOUNDER SEED: business lookup started");
            Guid businessGuid = Guid.Empty;
            string? businessName = null;
            using (var bizCmd = conn.CreateCommand())
            {
                bizCmd.CommandText = """
                    SELECT "Id", "Name" FROM "Businesses"
                    WHERE CAST("IsActive" AS TEXT) IN ('true','1','t')
                    ORDER BY "CreatedAtUtc" ASC
                """;
                using var r = bizCmd.ExecuteReader();
                while (r.Read())
                {
                    var rawId = r[0];
                    if (rawId is Guid g)
                    {
                        businessGuid = g;
                        businessName = r[1]?.ToString();
                        break;
                    }
                    if (Guid.TryParse(rawId?.ToString(), out var parsed))
                    {
                        businessGuid = parsed;
                        businessName = r[1]?.ToString();
                        break;
                    }
                    Log.Debug("FOUNDER SEED: skipping business with non-UUID id = {RawId}", rawId);
                }
            }
            if (businessGuid == Guid.Empty)
            {
                Log.Warning("FOUNDER SEED: no eligible business with valid GUID found");
                Log.Information("FOUNDER SEED: finished");
                return;
            }
            Log.Information("FOUNDER SEED: selected valid business id = {BizId}, name = {BizName}", businessGuid, businessName);

            Guid? existingUserId = null;
            using (var checkCmd = conn.CreateCommand())
            {
                checkCmd.CommandText = """SELECT "Id" FROM "BusinessUsers" WHERE "Email" = @email LIMIT 1""";
                var pe = checkCmd.CreateParameter(); pe.ParameterName = "email"; pe.Value = founderEmail; checkCmd.Parameters.Add(pe);
                var result = checkCmd.ExecuteScalar();
                if (result is Guid eg)
                    existingUserId = eg;
                else if (result is not null && result is not DBNull && Guid.TryParse(result.ToString(), out var eparsed))
                    existingUserId = eparsed;
            }
            Log.Information("FOUNDER SEED: founder lookup before write = {Result}", existingUserId.HasValue ? $"found (id={existingUserId.Value})" : "not found");

            var passwordHash = WhatsAppSaaS.Api.Controllers.AuthController.HashPassword(rawPassword);

            if (existingUserId.HasValue)
            {
                Log.Information("FOUNDER SEED: performing UPDATE");
                using var updCmd = conn.CreateCommand();
                updCmd.CommandText = """
                    UPDATE "BusinessUsers"
                    SET "PasswordHash" = @hash, "Role" = 'Founder', "IsActive" = true, "BusinessId" = @bizId, "Name" = @name
                    WHERE "Id" = @uid
                """;
                var p1 = updCmd.CreateParameter(); p1.ParameterName = "hash"; p1.Value = passwordHash; updCmd.Parameters.Add(p1);
                var p2 = updCmd.CreateParameter(); p2.ParameterName = "bizId"; p2.Value = businessGuid; updCmd.Parameters.Add(p2);
                var p3 = updCmd.CreateParameter(); p3.ParameterName = "name"; p3.Value = founderName; updCmd.Parameters.Add(p3);
                var p4 = updCmd.CreateParameter(); p4.ParameterName = "uid"; p4.Value = existingUserId.Value; updCmd.Parameters.Add(p4);
                var rows = updCmd.ExecuteNonQuery();
                Log.Information("FOUNDER SEED: rows affected = {Rows}", rows);
            }
            else
            {
                Log.Information("FOUNDER SEED: performing INSERT");
                var userId = Guid.NewGuid();
                using var insCmd = conn.CreateCommand();
                insCmd.CommandText = """
                    INSERT INTO "BusinessUsers" ("Id", "BusinessId", "Name", "Email", "PasswordHash", "Role", "IsActive", "CreatedAtUtc")
                    VALUES (@id, @bizId, @name, @email, @hash, 'Founder', true, @created)
                """;
                var p1 = insCmd.CreateParameter(); p1.ParameterName = "id"; p1.Value = userId; insCmd.Parameters.Add(p1);
                var p2 = insCmd.CreateParameter(); p2.ParameterName = "bizId"; p2.Value = businessGuid; insCmd.Parameters.Add(p2);
                var p3 = insCmd.CreateParameter(); p3.ParameterName = "name"; p3.Value = founderName; insCmd.Parameters.Add(p3);
                var p4 = insCmd.CreateParameter(); p4.ParameterName = "email"; p4.Value = founderEmail; insCmd.Parameters.Add(p4);
                var p5 = insCmd.CreateParameter(); p5.ParameterName = "hash"; p5.Value = passwordHash; insCmd.Parameters.Add(p5);
                var p6 = insCmd.CreateParameter(); p6.ParameterName = "created"; p6.Value = DateTime.UtcNow; insCmd.Parameters.Add(p6);
                var rows = insCmd.ExecuteNonQuery();
                Log.Information("FOUNDER SEED: rows affected = {Rows}", rows);
            }

            using (var verCmd = conn.CreateCommand())
            {
                verCmd.CommandText = """
                    SELECT u."Id", u."Email", u."Role", u."IsActive",
                           CAST(u."BusinessId" AS TEXT) AS "UBizId",
                           b."Name" AS "BizName"
                    FROM "BusinessUsers" u
                    INNER JOIN "Businesses" b ON CAST(b."Id" AS TEXT) = CAST(u."BusinessId" AS TEXT)
                    WHERE u."Email" = @email
                    LIMIT 1
                """;
                var pv = verCmd.CreateParameter(); pv.ParameterName = "email"; pv.Value = founderEmail; verCmd.Parameters.Add(pv);
                using var vr = verCmd.ExecuteReader();
                if (vr.Read())
                {
                    Log.Information("FOUNDER SEED: post-write verification = found");
                    Log.Information("FOUNDER SEED: verified email={Email}, bizId={BizId}, biz={BizName}, role={Role}, active={Active}",
                        vr["Email"], vr["UBizId"], vr["BizName"], vr["Role"], vr["IsActive"]);
                }
                else
                {
                    Log.Error("FOUNDER SEED: post-write verification = not found");
                    using var rawCmd = conn.CreateCommand();
                    rawCmd.CommandText = """SELECT "Id", "Email", CAST("BusinessId" AS TEXT) FROM "BusinessUsers" WHERE "Email" = @email LIMIT 1""";
                    var pr = rawCmd.CreateParameter(); pr.ParameterName = "email"; pr.Value = founderEmail; rawCmd.Parameters.Add(pr);
                    using var rr = rawCmd.ExecuteReader();
                    if (rr.Read())
                        Log.Error("FOUNDER SEED: user EXISTS in table (id={Id}, bizId={BizId}) but JOIN to Businesses FAILS — businessId mismatch?",
                            rr[0], rr[2]);
                    else
                        Log.Error("FOUNDER SEED: user does NOT exist in table at all after write — INSERT was silently lost?");
                }
            }

            // Demote any other users with role='Founder' that are NOT the current founder email
            using (var demoteCmd = conn.CreateCommand())
            {
                demoteCmd.CommandText = """
                    UPDATE "BusinessUsers" SET "Role" = 'Owner'
                    WHERE "Role" = 'Founder' AND "Email" != @email
                """;
                var pd = demoteCmd.CreateParameter(); pd.ParameterName = "email"; pd.Value = founderEmail; demoteCmd.Parameters.Add(pd);
                var demoted = demoteCmd.ExecuteNonQuery();
                if (demoted > 0) Log.Information("FOUNDER SEED: demoted {Count} stale Founder account(s) to Owner", demoted);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "FOUNDER SEED ERROR: {Message}", ex.ToString());
        }

        Log.Information("FOUNDER SEED: finished");
    }
}
