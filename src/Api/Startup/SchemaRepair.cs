using Serilog;

namespace WhatsAppSaaS.Api.Startup;

/// <summary>
/// Legacy schema repair: bring a partially-migrated production DB
/// in line with the current EF model BEFORE running migrations.
/// All statements are idempotent (IF NOT EXISTS / IF EXISTS).
///
/// Once the schema is stable (all migrations recorded, correct column types),
/// the full repair is skipped on subsequent startups.
/// </summary>
public static class SchemaRepair
{
    public static void RepairLegacySchema(System.Data.Common.DbConnection conn)
    {
        // ── Fast path: skip full repair if schema is already stable ──
        if (IsSchemaStable(conn))
        {
            Log.Information("LEGACY SCHEMA REPAIR: skipped (schema already stable)");
            return;
        }

        Log.Information("LEGACY SCHEMA REPAIR: schema needs repair — running full repair");

        RunFullRepair(conn);
    }

    /// <summary>
    /// Checks sentinel conditions to determine if the schema is already fully repaired.
    /// If all known migrations are recorded AND key columns have correct types, skip repair.
    /// </summary>
    private static bool IsSchemaStable(System.Data.Common.DbConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            // Check: (1) __EFMigrationsHistory exists and has our latest migration,
            //         (2) Businesses.Id is uuid (not text),
            //         (3) Orders.BusinessId column exists,
            //         (4) BusinessUsers table exists
            cmd.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM "__EFMigrationsHistory"
                     WHERE "MigrationId" = '20260315000747_AddMenuPdfUpload') AS has_latest_migration,
                    (SELECT COUNT(*) FROM information_schema.columns
                     WHERE table_schema='public' AND table_name='Businesses'
                       AND column_name='Id' AND data_type='uuid') AS biz_id_is_uuid,
                    (SELECT COUNT(*) FROM information_schema.columns
                     WHERE table_schema='public' AND table_name='Orders'
                       AND column_name='BusinessId') AS orders_has_biz_id,
                    (SELECT COUNT(*) FROM information_schema.tables
                     WHERE table_schema='public' AND table_name='BusinessUsers') AS has_biz_users
            """;

            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                var hasLatestMigration = Convert.ToInt32(r["has_latest_migration"]) > 0;
                var bizIdIsUuid = Convert.ToInt32(r["biz_id_is_uuid"]) > 0;
                var ordersHasBizId = Convert.ToInt32(r["orders_has_biz_id"]) > 0;
                var hasBizUsers = Convert.ToInt32(r["has_biz_users"]) > 0;

                return hasLatestMigration && bizIdIsUuid && ordersHasBizId && hasBizUsers;
            }
        }
        catch
        {
            // If check fails (e.g. __EFMigrationsHistory doesn't exist yet), repair is needed
        }
        return false;
    }

    private static void RunFullRepair(System.Data.Common.DbConnection conn)
    {
        Log.Information("LEGACY SCHEMA REPAIR START");

        // ── Remove legacy non-UUID business rows that block text→uuid conversion ──
        CleanupInvalidBusinessIds(conn);

        // Ensure __EFMigrationsHistory exists
        ExecSql(conn, """CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" ("MigrationId" varchar(150) NOT NULL PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL)""");

        // Remove falsely-recorded InitV2 if the schema is incomplete
        ExecSql(conn, """
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20260305063008_InitV2')
                   AND EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'Orders')
                   AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Orders' AND column_name = 'BusinessId')
                THEN
                    DELETE FROM "__EFMigrationsHistory"
                    WHERE "MigrationId" IN (
                        '20260305063008_InitV2',
                        '20260305161922_AddAnalyticsFields',
                        '20260305170406_AddCompositeIndexBusinessCheckout',
                        '20260305200000_FixBooleanColumnsForPostgres'
                    );
                    RAISE NOTICE 'Removed falsely-recorded migrations from history';
                END IF;
            END $$;
        """);

        // ── Tables (CREATE IF NOT EXISTS) ──
        ExecSql(conn, """CREATE TABLE IF NOT EXISTS "Businesses" ("Id" uuid NOT NULL PRIMARY KEY, "Name" text NOT NULL, "PhoneNumberId" text NOT NULL, "AccessToken" text NOT NULL, "AdminKey" text NOT NULL, "IsActive" boolean NOT NULL DEFAULT true, "CreatedAtUtc" timestamp NOT NULL DEFAULT now())""");
        ExecSql(conn, """CREATE TABLE IF NOT EXISTS "Customers" ("Id" uuid NOT NULL PRIMARY KEY, "BusinessId" uuid, "PhoneE164" text NOT NULL, "Name" text, "TotalSpent" numeric(12,2) NOT NULL DEFAULT 0, "OrdersCount" integer NOT NULL DEFAULT 0, "FirstSeenAtUtc" timestamp NOT NULL DEFAULT now(), "LastSeenAtUtc" timestamp, "LastPurchaseAtUtc" timestamp)""");
        ExecSql(conn, """CREATE TABLE IF NOT EXISTS "Products" ("Id" uuid NOT NULL PRIMARY KEY, "Name" text NOT NULL, "Price" numeric NOT NULL DEFAULT 0)""");
        ExecSql(conn, """CREATE TABLE IF NOT EXISTS "ConversationStates" ("ConversationId" varchar(256) NOT NULL PRIMARY KEY, "BusinessId" uuid, "UpdatedAtUtc" timestamp NOT NULL DEFAULT now(), "StateJson" text NOT NULL DEFAULT '{}')""");
        ExecSql(conn, """CREATE TABLE IF NOT EXISTS "ProcessedMessages" ("Id" uuid NOT NULL PRIMARY KEY, "ConversationId" varchar(256) NOT NULL, "MessageId" varchar(256) NOT NULL, "CreatedAtUtc" timestamp NOT NULL DEFAULT now())""");
        ExecSql(conn, """CREATE TABLE IF NOT EXISTS "Orders" ("Id" uuid NOT NULL PRIMARY KEY, "From" text NOT NULL, "PhoneNumberId" text NOT NULL, "DeliveryType" text NOT NULL DEFAULT 'pickup', "Status" text NOT NULL DEFAULT 'Pending', "CreatedAtUtc" timestamp NOT NULL DEFAULT now(), "CheckoutFormSent" boolean NOT NULL DEFAULT false, "CheckoutCompleted" boolean NOT NULL DEFAULT false)""");
        ExecSql(conn, """CREATE TABLE IF NOT EXISTS "OrderItems" ("Id" uuid NOT NULL PRIMARY KEY, "OrderId" uuid NOT NULL, "Name" text NOT NULL, "Quantity" integer NOT NULL DEFAULT 1, "UnitPrice" numeric(12,2), "LineTotal" numeric(12,2))""");

        // ── Fix SQLite-style text columns → uuid ──
        RepairTextToUuid(conn);

        // ── Missing columns on Orders (ADD COLUMN IF NOT EXISTS) ──
        string[] orderColumns =
        [
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "BusinessId" uuid""",
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "CustomerId" uuid""",
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "CustomerName" text""",
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "CustomerIdNumber" text""",
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "CustomerPhone" text""",
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "Address" text""",
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "PaymentMethod" text""",
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "ReceiverName" text""",
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "AdditionalNotes" text""",
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "LocationLat" numeric(9,6)""",
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "LocationLng" numeric(9,6)""",
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "LocationText" text""",
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "CheckoutCompletedAtUtc" timestamp""",
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "LastNotifiedStatus" text""",
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "LastNotifiedAtUtc" timestamp""",
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "SubtotalAmount" numeric(12,2)""",
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "TotalAmount" numeric(12,2)""",
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "AcceptedAtUtc" timestamp""",
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "DeliveredAtUtc" timestamp""",
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "DeliveryFee" numeric(12,2)""",
            """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "PreparingAtUtc" timestamp""",
        ];
        foreach (var sql in orderColumns)
        {
            if (ExecSql(conn, sql))
                Log.Information("LEGACY COLUMN ADDED: {Col}", sql.Replace("""ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS """, "Orders."));
        }

        // ── Missing columns on Businesses (payment + profile) ──
        string[] businessColumns =
        [
            """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "PaymentMobileBank" text""",
            """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "PaymentMobileId" text""",
            """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "PaymentMobilePhone" text""",
            """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "Greeting" text""",
            """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "Schedule" text""",
            """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "Address" text""",
            """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "LogoUrl" text""",
            """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "NotificationPhone" character varying(50)""",
            """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "RestaurantType" character varying(50)""",
        ];
        foreach (var sql in businessColumns)
        {
            if (ExecSql(conn, sql))
                Log.Information("LEGACY COLUMN ADDED: {Col}", sql.Replace("""ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS """, "Businesses."));
        }

        ExecSql(conn, """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "SpecialInstructions" text""");
        ExecSql(conn, """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "PaymentProofMediaId" text""");
        ExecSql(conn, """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "PaymentProofSubmittedAtUtc" timestamp""");
        ExecSql(conn, """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "PaymentVerifiedAtUtc" timestamp""");
        ExecSql(conn, """ALTER TABLE "Orders" ADD COLUMN IF NOT EXISTS "PaymentVerifiedBy" text""");
        ExecSql(conn, """ALTER TABLE "Customers" ADD COLUMN IF NOT EXISTS "LastDeliveryAddress" text""");

        // ── Menu system tables ──
        ExecSql(conn, """CREATE TABLE IF NOT EXISTS "MenuCategories" ("Id" uuid NOT NULL PRIMARY KEY, "BusinessId" uuid NOT NULL REFERENCES "Businesses"("Id") ON DELETE CASCADE, "Name" text NOT NULL, "SortOrder" integer NOT NULL DEFAULT 0, "IsActive" boolean NOT NULL DEFAULT true, "CreatedAtUtc" timestamp NOT NULL DEFAULT now())""");
        ExecSql(conn, """CREATE TABLE IF NOT EXISTS "MenuItems" ("Id" uuid NOT NULL PRIMARY KEY, "CategoryId" uuid NOT NULL REFERENCES "MenuCategories"("Id") ON DELETE CASCADE, "Name" text NOT NULL, "Price" numeric(12,2) NOT NULL DEFAULT 0, "Description" text, "IsAvailable" boolean NOT NULL DEFAULT true, "SortOrder" integer NOT NULL DEFAULT 0, "CreatedAtUtc" timestamp NOT NULL DEFAULT now())""");
        ExecSql(conn, """CREATE TABLE IF NOT EXISTS "MenuItemAliases" ("Id" uuid NOT NULL PRIMARY KEY, "MenuItemId" uuid NOT NULL REFERENCES "MenuItems"("Id") ON DELETE CASCADE, "Alias" text NOT NULL)""");
        ExecSql(conn, """CREATE INDEX IF NOT EXISTS "IX_MenuCategories_BusinessId" ON "MenuCategories" ("BusinessId")""");
        ExecSql(conn, """CREATE INDEX IF NOT EXISTS "IX_MenuItems_CategoryId" ON "MenuItems" ("CategoryId")""");
        ExecSql(conn, """CREATE INDEX IF NOT EXISTS "IX_MenuItemAliases_MenuItemId" ON "MenuItemAliases" ("MenuItemId")""");

        // ── BusinessUsers table ──
        if (!ExecSql(conn, """CREATE TABLE IF NOT EXISTS "BusinessUsers" ("Id" uuid NOT NULL PRIMARY KEY, "BusinessId" uuid NOT NULL REFERENCES "Businesses"("Id") ON DELETE CASCADE, "Name" text NOT NULL, "Email" text NOT NULL, "PasswordHash" text NOT NULL, "Role" text NOT NULL DEFAULT 'Operator', "IsActive" boolean NOT NULL DEFAULT true, "CreatedAtUtc" timestamp NOT NULL DEFAULT now())"""))
        {
            Log.Warning("LEGACY REPAIR: BusinessUsers with FK failed, creating without FK constraint");
            ExecSql(conn, """CREATE TABLE IF NOT EXISTS "BusinessUsers" ("Id" uuid NOT NULL PRIMARY KEY, "BusinessId" uuid NOT NULL, "Name" text NOT NULL, "Email" text NOT NULL, "PasswordHash" text NOT NULL, "Role" text NOT NULL DEFAULT 'Operator', "IsActive" boolean NOT NULL DEFAULT true, "CreatedAtUtc" timestamp NOT NULL DEFAULT now())""");
        }
        ExecSql(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_BusinessUsers_BusinessId_Email" ON "BusinessUsers" ("BusinessId", "Email")""");

        // ── BackgroundJobs table ──
        ExecSql(conn, """CREATE TABLE IF NOT EXISTS "BackgroundJobs" ("Id" uuid NOT NULL PRIMARY KEY, "JobType" varchar(100) NOT NULL, "PayloadJson" text NOT NULL DEFAULT '{}', "Status" varchar(20) NOT NULL DEFAULT 'Pending', "RetryCount" integer NOT NULL DEFAULT 0, "MaxRetries" integer NOT NULL DEFAULT 3, "LastError" varchar(2000), "ScheduledAtUtc" timestamp NOT NULL DEFAULT now(), "LockedAtUtc" timestamp, "CompletedAtUtc" timestamp, "BusinessId" uuid)""");
        ExecSql(conn, """CREATE INDEX IF NOT EXISTS "IX_BackgroundJobs_Status_ScheduledAtUtc" ON "BackgroundJobs" ("Status", "ScheduledAtUtc")""");

        // ── ExchangeRates table ──
        ExecSql(conn, """CREATE TABLE IF NOT EXISTS "ExchangeRates" ("Id" uuid NOT NULL PRIMARY KEY, "RateDate" timestamp NOT NULL, "UsdRate" numeric(12,2) NOT NULL, "EurRate" numeric(12,2) NOT NULL, "Source" varchar(50) NOT NULL DEFAULT 'bcv', "FetchedAtUtc" timestamp NOT NULL DEFAULT now())""");
        ExecSql(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExchangeRates_RateDate" ON "ExchangeRates" ("RateDate")""");

        // ── PasswordResetTokens table ──
        ExecSql(conn, """CREATE TABLE IF NOT EXISTS "PasswordResetTokens" ("Id" uuid NOT NULL PRIMARY KEY, "UserId" uuid NOT NULL, "TokenHash" varchar(128) NOT NULL, "ExpiresAtUtc" timestamp NOT NULL, "UsedAtUtc" timestamp, "CreatedAtUtc" timestamp NOT NULL DEFAULT now())""");
        ExecSql(conn, """CREATE INDEX IF NOT EXISTS "IX_PasswordResetTokens_TokenHash" ON "PasswordResetTokens" ("TokenHash")""");

        // ── CurrencyReference column on Businesses ──
        ExecSql(conn, """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "CurrencyReference" varchar(20)""");
        ExecSql(conn, """UPDATE "Businesses" SET "CurrencyReference" = 'BCV_USD' WHERE "CurrencyReference" IS NULL""");

        // ── VerticalType column on Businesses ──
        ExecSql(conn, """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "VerticalType" varchar(30) DEFAULT 'restaurant'""");
        ExecSql(conn, """UPDATE "Businesses" SET "VerticalType" = 'restaurant' WHERE "VerticalType" IS NULL""");

        // ── Per-business menu PDF ──
        ExecSql(conn, """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "MenuPdfUrl" varchar(500)""");
        ExecSql(conn, """CREATE TABLE IF NOT EXISTS "MenuPdfs" ("Id" uuid NOT NULL PRIMARY KEY, "BusinessId" uuid NOT NULL REFERENCES "Businesses"("Id") ON DELETE CASCADE, "Data" bytea NOT NULL, "ContentType" varchar(100) NOT NULL DEFAULT 'application/pdf', "UploadedAtUtc" timestamp NOT NULL DEFAULT now())""");
        ExecSql(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_MenuPdfs_BusinessId" ON "MenuPdfs" ("BusinessId")""");

        // ── Boolean column repair ──
        string[] boolRepairs =
        [
            """DO $$ BEGIN IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Businesses' AND column_name='IsActive' AND data_type<>'boolean') THEN ALTER TABLE "Businesses" ALTER COLUMN "IsActive" TYPE boolean USING CASE WHEN "IsActive"=0 THEN false ELSE true END; END IF; END $$""",
            """DO $$ BEGIN IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='CheckoutCompleted' AND data_type<>'boolean') THEN ALTER TABLE "Orders" ALTER COLUMN "CheckoutCompleted" TYPE boolean USING CASE WHEN "CheckoutCompleted"=0 THEN false ELSE true END; END IF; END $$""",
            """DO $$ BEGIN IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Orders' AND column_name='CheckoutFormSent' AND data_type<>'boolean') THEN ALTER TABLE "Orders" ALTER COLUMN "CheckoutFormSent" TYPE boolean USING CASE WHEN "CheckoutFormSent"=0 THEN false ELSE true END; END IF; END $$""",
        ];
        var boolFixed = false;
        foreach (var sql in boolRepairs)
        {
            if (ExecSql(conn, sql)) boolFixed = true;
        }
        Log.Information(boolFixed ? "LEGACY BOOL REPAIR APPLIED" : "LEGACY BOOL REPAIR SKIPPED");

        // ── Indexes (IF NOT EXISTS) ──
        ExecSql(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_Businesses_PhoneNumberId" ON "Businesses" ("PhoneNumberId")""");
        ExecSql(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_Customers_BusinessId_PhoneE164" ON "Customers" ("BusinessId", "PhoneE164")""");
        ExecSql(conn, """CREATE INDEX IF NOT EXISTS "IX_OrderItems_OrderId" ON "OrderItems" ("OrderId")""");
        ExecSql(conn, """CREATE INDEX IF NOT EXISTS "IX_Orders_CustomerId" ON "Orders" ("CustomerId")""");
        ExecSql(conn, """CREATE INDEX IF NOT EXISTS "IX_Orders_BusinessId" ON "Orders" ("BusinessId")""");
        ExecSql(conn, """CREATE INDEX IF NOT EXISTS "IX_Orders_CreatedAtUtc" ON "Orders" ("CreatedAtUtc")""");
        ExecSql(conn, """CREATE INDEX IF NOT EXISTS "IX_Orders_BusinessId_CheckoutCompleted" ON "Orders" ("BusinessId", "CheckoutCompleted")""");
        ExecSql(conn, """CREATE UNIQUE INDEX IF NOT EXISTS "IX_ProcessedMessages_ConversationId_MessageId" ON "ProcessedMessages" ("ConversationId", "MessageId")""");

        // ── Foreign keys (check before adding) ──
        AddFkIfMissing(conn, "FK_ProcessedMessages_ConversationStates_ConversationId",
            """ALTER TABLE "ProcessedMessages" ADD CONSTRAINT "FK_ProcessedMessages_ConversationStates_ConversationId" FOREIGN KEY ("ConversationId") REFERENCES "ConversationStates" ("ConversationId") ON DELETE CASCADE""");
        AddFkIfMissing(conn, "FK_Orders_Customers_CustomerId",
            """ALTER TABLE "Orders" ADD CONSTRAINT "FK_Orders_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES "Customers" ("Id") ON DELETE SET NULL""");
        AddFkIfMissing(conn, "FK_OrderItems_Orders_OrderId",
            """ALTER TABLE "OrderItems" ADD CONSTRAINT "FK_OrderItems_Orders_OrderId" FOREIGN KEY ("OrderId") REFERENCES "Orders" ("Id") ON DELETE CASCADE""");

        // ── Mark all known migrations as applied ──
        string[] allMigrations =
        [
            "20260305063008_InitV2",
            "20260305161922_AddAnalyticsFields",
            "20260305170406_AddCompositeIndexBusinessCheckout",
            "20260305200000_FixBooleanColumnsForPostgres",
            "20260306182317_AddBusinessPaymentMobileFields",
            "20260306202633_AddSpecialInstructions",
            "20260307035321_AddMenuSystem",
            "20260307044447_AddBusinessProfile",
            "20260307182204_AddCustomerLastDeliveryAddress",
            "20260307184701_AddBusinessNotificationPhone",
            "20260307191229_AddPaymentProofFields",
            "20260307193423_AddBusinessUsers",
            "20260307212018_AddRestaurantType",
            "20260308012829_AddBackgroundJobs",
            "20260309174326_AddExchangeRatesAndCurrencyRef",
            "20260309201857_AddVerticalType",
            "20260315000747_AddMenuPdfUpload",
        ];
        foreach (var mid in allMigrations)
        {
            ExecSql(conn, $"""INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('{mid}', '8.0.11') ON CONFLICT ("MigrationId") DO NOTHING""");
        }

        Log.Information("LEGACY SCHEMA REPAIR DONE");
    }

    /// <summary>
    /// Removes rows from Businesses where Id is not a valid UUID (e.g. "biz_demo_001").
    /// These legacy demo rows block the text→uuid column type conversion.
    /// Also cleans up any FK references in related tables.
    /// </summary>
    private static void CleanupInvalidBusinessIds(System.Data.Common.DbConnection conn)
    {
        try
        {
            // Check if Businesses.Id is still text type (cleanup only needed during text→uuid transition)
            using var typeCheck = conn.CreateCommand();
            typeCheck.CommandText = """
                SELECT data_type FROM information_schema.columns
                WHERE table_schema='public' AND table_name='Businesses' AND column_name='Id'
            """;
            var dataType = typeCheck.ExecuteScalar()?.ToString();
            if (dataType != "text")
            {
                // Column is already uuid — no invalid IDs possible
                return;
            }

            // Find non-UUID rows
            using var findCmd = conn.CreateCommand();
            findCmd.CommandText = """
                SELECT "Id", "Name" FROM "Businesses"
                WHERE CAST("Id" AS TEXT) !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
            """;
            var invalidIds = new List<(string id, string? name)>();
            using (var r = findCmd.ExecuteReader())
            {
                while (r.Read())
                    invalidIds.Add((r[0]?.ToString() ?? "", r[1]?.ToString()));
            }

            if (invalidIds.Count == 0)
            {
                Log.Information("LEGACY ID CLEANUP: no invalid business IDs found");
                return;
            }

            foreach (var (id, name) in invalidIds)
            {
                Log.Warning("LEGACY ID CLEANUP: removing invalid business Id={Id} Name={Name}", id, name);

                // Delete referencing rows in tables that have BusinessId FK (text column)
                // These are all demo/orphan data from the legacy row
                string[] relatedTables = ["Orders", "OrderItems", "Customers", "ConversationStates",
                    "MenuCategories", "MenuItems", "MenuItemAliases", "MenuPdfs",
                    "BusinessUsers", "BackgroundJobs"];

                foreach (var table in relatedTables)
                {
                    try
                    {
                        using var delRef = conn.CreateCommand();
                        // Use text cast to safely match regardless of column type
                        delRef.CommandText = table switch
                        {
                            // OrderItems references Orders, not Businesses directly
                            "OrderItems" => $"""
                                DELETE FROM "OrderItems" WHERE CAST("OrderId" AS TEXT) IN (
                                    SELECT CAST("Id" AS TEXT) FROM "Orders" WHERE CAST("BusinessId" AS TEXT) = @bid
                                )
                            """,
                            // MenuItems references MenuCategories, not Businesses directly
                            "MenuItems" => $"""
                                DELETE FROM "MenuItems" WHERE CAST("CategoryId" AS TEXT) IN (
                                    SELECT CAST("Id" AS TEXT) FROM "MenuCategories" WHERE CAST("BusinessId" AS TEXT) = @bid
                                )
                            """,
                            // MenuItemAliases references MenuItems
                            "MenuItemAliases" => $"""
                                DELETE FROM "MenuItemAliases" WHERE CAST("MenuItemId" AS TEXT) IN (
                                    SELECT CAST("Id" AS TEXT) FROM "MenuItems" WHERE CAST("CategoryId" AS TEXT) IN (
                                        SELECT CAST("Id" AS TEXT) FROM "MenuCategories" WHERE CAST("BusinessId" AS TEXT) = @bid
                                    )
                                )
                            """,
                            // Direct BusinessId reference
                            _ => $"""DELETE FROM "{table}" WHERE CAST("BusinessId" AS TEXT) = @bid"""
                        };
                        var p = delRef.CreateParameter();
                        p.ParameterName = "bid";
                        p.Value = id;
                        delRef.Parameters.Add(p);
                        var rows = delRef.ExecuteNonQuery();
                        if (rows > 0)
                            Log.Information("LEGACY ID CLEANUP: deleted {Rows} row(s) from {Table} referencing {Id}", rows, table, id);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "LEGACY ID CLEANUP: {Table} cleanup skipped for {Id} (table may not exist yet)", table, id);
                    }
                }

                // Delete the business row itself
                using var delBiz = conn.CreateCommand();
                delBiz.CommandText = """DELETE FROM "Businesses" WHERE CAST("Id" AS TEXT) = @bid""";
                var pb = delBiz.CreateParameter();
                pb.ParameterName = "bid";
                pb.Value = id;
                delBiz.Parameters.Add(pb);
                delBiz.ExecuteNonQuery();
                Log.Information("LEGACY ID CLEANUP: deleted business Id={Id}", id);
            }

            Log.Information("LEGACY ID CLEANUP: removed {Count} invalid business row(s)", invalidIds.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LEGACY ID CLEANUP: failed (non-fatal, text→uuid repair may still fail)");
        }
    }

    public static bool ExecSql(System.Data.Common.DbConnection conn, string sql)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning("LEGACY REPAIR SQL warning: {Message} — SQL: {Sql}", ex.Message, sql[..Math.Min(sql.Length, 120)]);
            return false;
        }
    }

    private static void AddFkIfMissing(System.Data.Common.DbConnection conn, string constraintName, string alterSql)
    {
        try
        {
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = $"SELECT 1 FROM pg_constraint WHERE conname = '{constraintName}'";
            var exists = checkCmd.ExecuteScalar() is not null;
            if (!exists)
                ExecSql(conn, alterSql);
        }
        catch (Exception ex)
        {
            Log.Warning("LEGACY FK check warning: {Message}", ex.Message);
        }
    }

    private static void RepairTextToUuid(System.Data.Common.DbConnection conn)
    {
        bool needsFix;
        try
        {
            using var checkCmd = conn.CreateCommand();
            checkCmd.CommandText = """
                SELECT 1 FROM information_schema.columns
                WHERE table_schema='public' AND table_name='Businesses' AND column_name='Id' AND data_type='text'
            """;
            needsFix = checkCmd.ExecuteScalar() is not null;
        }
        catch { return; }

        if (!needsFix)
        {
            Log.Information("TEXT→UUID REPAIR: not needed (columns already uuid)");
            return;
        }

        Log.Warning("TEXT→UUID REPAIR: detected text-typed Guid columns from SQLite migration — converting to uuid");

        try
        {
            using var fkCmd = conn.CreateCommand();
            fkCmd.CommandText = """
                DO $$ DECLARE r RECORD;
                BEGIN
                  FOR r IN (
                    SELECT tc.constraint_name, tc.table_name
                    FROM information_schema.table_constraints tc
                    WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = 'public'
                  ) LOOP
                    EXECUTE format('ALTER TABLE %I DROP CONSTRAINT IF EXISTS %I', r.table_name, r.constraint_name);
                  END LOOP;
                END $$;
            """;
            fkCmd.ExecuteNonQuery();
            Log.Information("TEXT→UUID REPAIR: dropped all FK constraints");
        }
        catch (Exception ex)
        {
            Log.Warning("TEXT→UUID REPAIR: FK drop warning: {Msg}", ex.Message);
        }

        (string table, string column)[] uuidColumns =
        [
            ("Businesses", "Id"),
            ("Customers", "Id"),
            ("Products", "Id"),
            ("ProcessedMessages", "Id"),
            ("Orders", "Id"),
            ("OrderItems", "Id"),
            ("MenuCategories", "Id"),
            ("MenuItems", "Id"),
            ("MenuItemAliases", "Id"),
            ("BusinessUsers", "Id"),
            ("BackgroundJobs", "Id"),
            ("ExchangeRates", "Id"),
            ("MenuPdfs", "Id"),
            ("Customers", "BusinessId"),
            ("ConversationStates", "BusinessId"),
            ("Orders", "BusinessId"),
            ("Orders", "CustomerId"),
            ("OrderItems", "OrderId"),
            ("MenuCategories", "BusinessId"),
            ("MenuItems", "CategoryId"),
            ("MenuItemAliases", "MenuItemId"),
            ("BusinessUsers", "BusinessId"),
            ("BackgroundJobs", "BusinessId"),
            ("MenuPdfs", "BusinessId"),
        ];

        foreach (var (table, column) in uuidColumns)
        {
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"""
                    DO $$ BEGIN
                      IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema='public' AND table_name='{table}' AND column_name='{column}' AND data_type='text'
                      ) THEN
                        ALTER TABLE "{table}" ALTER COLUMN "{column}" TYPE uuid USING "{column}"::uuid;
                        RAISE NOTICE 'Converted %.% text→uuid', '{table}', '{column}';
                      END IF;
                    END $$;
                """;
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Log.Warning("TEXT→UUID REPAIR: failed {Table}.{Col}: {Msg}", table, column, ex.Message);
            }
        }

        Log.Information("TEXT→UUID REPAIR: done");
    }
}
