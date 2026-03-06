using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Converts boolean columns from INTEGER (created by SQLite-generated migrations)
    /// to native boolean on Postgres. Idempotent — skips columns already typed as boolean.
    /// </summary>
    public partial class FixBooleanColumnsForPostgres : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    DO $$
                    BEGIN
                        -- Businesses.IsActive
                        IF EXISTS (
                            SELECT 1 FROM information_schema.columns
                            WHERE table_name = 'Businesses'
                              AND column_name = 'IsActive'
                              AND data_type <> 'boolean'
                        ) THEN
                            ALTER TABLE "Businesses"
                                ALTER COLUMN "IsActive" TYPE boolean
                                USING CASE WHEN "IsActive" = 0 THEN false ELSE true END;
                        END IF;

                        -- Orders.CheckoutCompleted
                        IF EXISTS (
                            SELECT 1 FROM information_schema.columns
                            WHERE table_name = 'Orders'
                              AND column_name = 'CheckoutCompleted'
                              AND data_type <> 'boolean'
                        ) THEN
                            ALTER TABLE "Orders"
                                ALTER COLUMN "CheckoutCompleted" TYPE boolean
                                USING CASE WHEN "CheckoutCompleted" = 0 THEN false ELSE true END;
                        END IF;

                        -- Orders.CheckoutFormSent
                        IF EXISTS (
                            SELECT 1 FROM information_schema.columns
                            WHERE table_name = 'Orders'
                              AND column_name = 'CheckoutFormSent'
                              AND data_type <> 'boolean'
                        ) THEN
                            ALTER TABLE "Orders"
                                ALTER COLUMN "CheckoutFormSent" TYPE boolean
                                USING CASE WHEN "CheckoutFormSent" = 0 THEN false ELSE true END;
                        END IF;
                    END $$;
                    """);
            }
            // SQLite: no-op (INTEGER is the native bool representation)
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — reverting boolean to integer is not needed
            // and would re-introduce the same bug.
        }
    }
}
