using Microsoft.EntityFrameworkCore;
using Serilog;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Startup;

/// <summary>
/// Handles EF migrations with Postgres advisory locking, legacy schema repair,
/// and post-migration column type fixes.
/// </summary>
public static class MigrationRunner
{
    public static void Run(IServiceProvider services)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var isNpgsql = db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;

        if (isNpgsql)
        {
            ApplyWithAdvisoryLock(db);
        }
        else
        {
            try
            {
                Log.Information("MIGRATE START (SQLite)");
                db.Database.Migrate();
                Log.Information("MIGRATE OK (SQLite)");
            }
            catch (Exception ex)
            {
                Log.Fatal(ex, "Failed applying EF migrations on startup.");
                throw;
            }
        }
        Log.Information("STARTUP MIGRATIONS completed in {Elapsed}ms", sw.ElapsedMilliseconds);
    }

    private static void ApplyWithAdvisoryLock(AppDbContext db)
    {
        const long lockId = 920_717;

        var conn = db.Database.GetDbConnection();
        conn.Open();
        try
        {
            Log.Information("MIGRATION LOCK WAITING...");
            using (var lockCmd = conn.CreateCommand())
            {
                lockCmd.CommandText = $"SET lock_timeout = '30s'; SELECT pg_advisory_lock({lockId})";
                lockCmd.CommandTimeout = 40;
                lockCmd.ExecuteNonQuery();
            }
            Log.Information("MIGRATION LOCK ACQUIRED");

            try
            {
                // Step 1: Full legacy schema repair
                SchemaRepair.RepairLegacySchema(conn);

                // Step 2: Normal EF migrations
                var pending = db.Database.GetPendingMigrations().ToList();
                if (pending.Count == 0)
                {
                    Log.Information("MIGRATE: no pending migrations (Postgres)");
                }
                else
                {
                    Log.Information("MIGRATE: {Count} pending: {List}", pending.Count, string.Join(", ", pending));
                    db.Database.Migrate();
                    Log.Information("MIGRATE OK (Postgres)");
                }

                // Step 3: Fix SQLite-generated column types (idempotent)
                FixColumnTypes(conn);
            }
            finally
            {
                try
                {
                    using var unlockCmd = conn.CreateCommand();
                    unlockCmd.CommandText = $"SELECT pg_advisory_unlock({lockId})";
                    unlockCmd.ExecuteNonQuery();
                    Log.Information("MIGRATION LOCK RELEASED");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "MIGRATION LOCK RELEASE failed (non-fatal)");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed applying EF migrations on startup.");
            throw;
        }
        finally
        {
            conn.Close();
        }
    }

    private static void FixColumnTypes(System.Data.Common.DbConnection conn)
    {
        try
        {
            // Fast path: check if sentinel columns already have correct types
            if (AreColumnTypesCorrect(conn))
            {
                Log.Information("SCHEMA FIX: skipped (column types already correct)");
                return;
            }

            Log.Information("SCHEMA FIX: executing column type conversions...");
            using var fixCmd = conn.CreateCommand();
            fixCmd.CommandText = """
                DO $$
                DECLARE
                    fixed int := 0;
                BEGIN
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='SubtotalAmount' AND data_type='text') THEN
                        ALTER TABLE "Orders" ALTER COLUMN "SubtotalAmount" TYPE numeric USING CASE WHEN btrim("SubtotalAmount")='' THEN NULL ELSE "SubtotalAmount"::numeric END;
                        fixed := fixed + 1;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='TotalAmount' AND data_type='text') THEN
                        ALTER TABLE "Orders" ALTER COLUMN "TotalAmount" TYPE numeric USING CASE WHEN btrim("TotalAmount")='' THEN NULL ELSE "TotalAmount"::numeric END;
                        fixed := fixed + 1;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='DeliveryFee' AND data_type='text') THEN
                        ALTER TABLE "Orders" ALTER COLUMN "DeliveryFee" TYPE numeric USING CASE WHEN btrim("DeliveryFee")='' THEN NULL ELSE "DeliveryFee"::numeric END;
                        fixed := fixed + 1;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='LocationLat' AND data_type='text') THEN
                        ALTER TABLE "Orders" ALTER COLUMN "LocationLat" TYPE numeric USING CASE WHEN btrim("LocationLat")='' THEN NULL ELSE "LocationLat"::numeric END;
                        fixed := fixed + 1;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='LocationLng' AND data_type='text') THEN
                        ALTER TABLE "Orders" ALTER COLUMN "LocationLng" TYPE numeric USING CASE WHEN btrim("LocationLng")='' THEN NULL ELSE "LocationLng"::numeric END;
                        fixed := fixed + 1;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='CashTenderedAmount' AND data_type='text') THEN
                        ALTER TABLE "Orders" ALTER COLUMN "CashTenderedAmount" TYPE numeric USING CASE WHEN btrim("CashTenderedAmount")='' THEN NULL ELSE "CashTenderedAmount"::numeric END;
                        fixed := fixed + 1;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='CashBcvRateUsed' AND data_type='text') THEN
                        ALTER TABLE "Orders" ALTER COLUMN "CashBcvRateUsed" TYPE numeric USING CASE WHEN btrim("CashBcvRateUsed")='' THEN NULL ELSE "CashBcvRateUsed"::numeric END;
                        fixed := fixed + 1;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='CashChangeAmount' AND data_type='text') THEN
                        ALTER TABLE "Orders" ALTER COLUMN "CashChangeAmount" TYPE numeric USING CASE WHEN btrim("CashChangeAmount")='' THEN NULL ELSE "CashChangeAmount"::numeric END;
                        fixed := fixed + 1;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='CashChangeAmountBs' AND data_type='text') THEN
                        ALTER TABLE "Orders" ALTER COLUMN "CashChangeAmountBs" TYPE numeric USING CASE WHEN btrim("CashChangeAmountBs")='' THEN NULL ELSE "CashChangeAmountBs"::numeric END;
                        fixed := fixed + 1;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='CashChangeRequired' AND data_type<>'boolean') THEN
                        ALTER TABLE "Orders" ALTER COLUMN "CashChangeRequired" DROP DEFAULT;
                        ALTER TABLE "Orders" ALTER COLUMN "CashChangeRequired" TYPE boolean USING CASE WHEN "CashChangeRequired"::text IN ('1','true','t') THEN true ELSE false END;
                        ALTER TABLE "Orders" ALTER COLUMN "CashChangeRequired" SET DEFAULT false;
                        fixed := fixed + 1;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='CashChangeReturned' AND data_type<>'boolean') THEN
                        ALTER TABLE "Orders" ALTER COLUMN "CashChangeReturned" DROP DEFAULT;
                        ALTER TABLE "Orders" ALTER COLUMN "CashChangeReturned" TYPE boolean USING CASE WHEN "CashChangeReturned"::text IN ('1','true','t') THEN true ELSE false END;
                        ALTER TABLE "Orders" ALTER COLUMN "CashChangeReturned" SET DEFAULT false;
                        fixed := fixed + 1;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='OrderItems' AND column_name='UnitPrice' AND data_type='text') THEN
                        ALTER TABLE "OrderItems" ALTER COLUMN "UnitPrice" TYPE numeric USING CASE WHEN btrim("UnitPrice")='' THEN NULL ELSE "UnitPrice"::numeric END;
                        fixed := fixed + 1;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='OrderItems' AND column_name='LineTotal' AND data_type='text') THEN
                        ALTER TABLE "OrderItems" ALTER COLUMN "LineTotal" TYPE numeric USING CASE WHEN btrim("LineTotal")='' THEN NULL ELSE "LineTotal"::numeric END;
                        fixed := fixed + 1;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='MenuItems' AND column_name='Price' AND data_type='text') THEN
                        ALTER TABLE "MenuItems" ALTER COLUMN "Price" TYPE numeric USING CASE WHEN btrim("Price")='' THEN NULL ELSE "Price"::numeric END;
                        fixed := fixed + 1;
                    END IF;
                    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Customers' AND column_name='TotalSpent' AND data_type='text') THEN
                        ALTER TABLE "Customers" ALTER COLUMN "TotalSpent" TYPE numeric USING CASE WHEN btrim("TotalSpent")='' THEN NULL ELSE "TotalSpent"::numeric END;
                        fixed := fixed + 1;
                    END IF;
                    RAISE NOTICE 'Schema fix: % columns converted', fixed;
                END $$;
            """;
            fixCmd.ExecuteNonQuery();
            Log.Information("SCHEMA FIX: column type conversion complete — verifying...");

            using var verifyCmd = conn.CreateCommand();
            verifyCmd.CommandText = """
                SELECT table_name, column_name, data_type
                FROM information_schema.columns
                WHERE (table_name, column_name) IN (
                    ('Orders','SubtotalAmount'),('Orders','TotalAmount'),('Orders','DeliveryFee'),
                    ('Orders','CashChangeRequired'),('Orders','CashChangeReturned'),
                    ('Orders','CashTenderedAmount'),('Orders','CashChangeAmount'),
                    ('OrderItems','UnitPrice'),('OrderItems','LineTotal'),
                    ('MenuItems','Price'),('Customers','TotalSpent')
                )
                ORDER BY table_name, column_name
            """;
            using var vr = verifyCmd.ExecuteReader();
            while (vr.Read())
            {
                var tbl = vr.GetString(0);
                var col = vr.GetString(1);
                var dtype = vr.GetString(2);
                var ok = dtype is "numeric" or "boolean";
                if (ok)
                    Log.Information("SCHEMA VERIFY: {Table}.{Column} = {Type} OK", tbl, col, dtype);
                else
                    Log.Error("SCHEMA VERIFY: {Table}.{Column} = {Type} STILL WRONG", tbl, col, dtype);
            }
        }
        catch (Npgsql.PostgresException pgEx)
        {
            Log.Fatal(pgEx, "SCHEMA FIX FAILED (Postgres): SqlState={SqlState} Table={Table} Column={Column} " +
                "Constraint={Constraint} MessageText={Msg} Detail={Detail} Hint={Hint}",
                pgEx.SqlState, pgEx.TableName, pgEx.ColumnName,
                pgEx.ConstraintName, pgEx.MessageText, pgEx.Detail, pgEx.Hint);
            throw;
        }
        catch (Exception schemaEx)
        {
            Log.Fatal(schemaEx, "SCHEMA FIX FAILED: {Type}: {Message}", schemaEx.GetType().Name, schemaEx.Message);
            throw;
        }
    }

    /// <summary>
    /// Quick check: if sentinel columns are already numeric/boolean, skip the full fix pass.
    /// Checks Orders.SubtotalAmount (numeric) and Orders.CashChangeRequired (boolean) as sentinels.
    /// </summary>
    private static bool AreColumnTypesCorrect(System.Data.Common.DbConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*) FROM information_schema.columns
                WHERE table_schema='public' AND (
                    (table_name='Orders' AND column_name='SubtotalAmount' AND data_type='numeric')
                    OR
                    (table_name='Orders' AND column_name='CashChangeRequired' AND data_type='boolean')
                )
            """;
            var count = Convert.ToInt32(cmd.ExecuteScalar());
            return count == 2; // Both sentinel columns have correct types
        }
        catch
        {
            return false; // If check fails, run the full fix
        }
    }
}
