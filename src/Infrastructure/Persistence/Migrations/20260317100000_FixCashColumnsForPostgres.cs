using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Fixes cash-change columns created by SQLite-generated migration (AddCashChangeFields).
    /// On PostgreSQL: converts boolean columns from INTEGER to boolean,
    /// and decimal columns from TEXT to numeric.
    /// Idempotent — skips columns already correctly typed.
    /// </summary>
    public partial class FixCashColumnsForPostgres : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    DO $$
                    BEGIN
                        -- Orders.CashChangeRequired: INTEGER → boolean
                        IF EXISTS (
                            SELECT 1 FROM information_schema.columns
                            WHERE table_name = 'Orders'
                              AND column_name = 'CashChangeRequired'
                              AND data_type <> 'boolean'
                        ) THEN
                            ALTER TABLE "Orders"
                                ALTER COLUMN "CashChangeRequired" TYPE boolean
                                USING CASE WHEN "CashChangeRequired"::text = '0' THEN false ELSE true END;
                        END IF;

                        -- Orders.CashChangeReturned: INTEGER → boolean
                        IF EXISTS (
                            SELECT 1 FROM information_schema.columns
                            WHERE table_name = 'Orders'
                              AND column_name = 'CashChangeReturned'
                              AND data_type <> 'boolean'
                        ) THEN
                            ALTER TABLE "Orders"
                                ALTER COLUMN "CashChangeReturned" TYPE boolean
                                USING CASE WHEN "CashChangeReturned"::text = '0' THEN false ELSE true END;
                        END IF;

                        -- Orders.CashTenderedAmount: TEXT → numeric
                        IF EXISTS (
                            SELECT 1 FROM information_schema.columns
                            WHERE table_name = 'Orders'
                              AND column_name = 'CashTenderedAmount'
                              AND data_type = 'text'
                        ) THEN
                            ALTER TABLE "Orders"
                                ALTER COLUMN "CashTenderedAmount" TYPE numeric
                                USING CASE WHEN "CashTenderedAmount" IS NULL OR "CashTenderedAmount" = '' THEN NULL ELSE "CashTenderedAmount"::numeric END;
                        END IF;

                        -- Orders.CashBcvRateUsed: TEXT → numeric
                        IF EXISTS (
                            SELECT 1 FROM information_schema.columns
                            WHERE table_name = 'Orders'
                              AND column_name = 'CashBcvRateUsed'
                              AND data_type = 'text'
                        ) THEN
                            ALTER TABLE "Orders"
                                ALTER COLUMN "CashBcvRateUsed" TYPE numeric
                                USING CASE WHEN "CashBcvRateUsed" IS NULL OR "CashBcvRateUsed" = '' THEN NULL ELSE "CashBcvRateUsed"::numeric END;
                        END IF;

                        -- Orders.CashChangeAmount: TEXT → numeric
                        IF EXISTS (
                            SELECT 1 FROM information_schema.columns
                            WHERE table_name = 'Orders'
                              AND column_name = 'CashChangeAmount'
                              AND data_type = 'text'
                        ) THEN
                            ALTER TABLE "Orders"
                                ALTER COLUMN "CashChangeAmount" TYPE numeric
                                USING CASE WHEN "CashChangeAmount" IS NULL OR "CashChangeAmount" = '' THEN NULL ELSE "CashChangeAmount"::numeric END;
                        END IF;

                        -- Orders.CashChangeAmountBs: TEXT → numeric
                        IF EXISTS (
                            SELECT 1 FROM information_schema.columns
                            WHERE table_name = 'Orders'
                              AND column_name = 'CashChangeAmountBs'
                              AND data_type = 'text'
                        ) THEN
                            ALTER TABLE "Orders"
                                ALTER COLUMN "CashChangeAmountBs" TYPE numeric
                                USING CASE WHEN "CashChangeAmountBs" IS NULL OR "CashChangeAmountBs" = '' THEN NULL ELSE "CashChangeAmountBs"::numeric END;
                        END IF;
                    END $$;
                    """);
            }
            // SQLite: no-op (INTEGER/TEXT are the native representations)
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — reverting types would re-introduce the bug.
        }
    }
}
