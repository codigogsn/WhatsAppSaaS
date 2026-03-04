using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FixPriceColumnsToNumeric : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ✅ IMPORTANT:
            // This migration contains Postgres-specific SQL.
            // When running locally with SQLite, we NO-OP to avoid "near DO: syntax error".
            if (ActiveProvider.Contains("Npgsql"))
            {
                migrationBuilder.Sql(@"
DO $$
BEGIN

    -- OrderItems.UnitPrice
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema='public'
          AND table_name='OrderItems'
          AND column_name='UnitPrice'
          AND data_type='text'
    ) THEN
        ALTER TABLE ""OrderItems""
        ALTER COLUMN ""UnitPrice""
        TYPE numeric(12,2)
        USING (
            CASE
                WHEN ""UnitPrice"" IS NULL THEN NULL
                WHEN btrim(""UnitPrice"") = '' THEN NULL
                ELSE replace(btrim(""UnitPrice""), ',', '.')::numeric
            END
        );
    END IF;

    -- OrderItems.LineTotal
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema='public'
          AND table_name='OrderItems'
          AND column_name='LineTotal'
          AND data_type='text'
    ) THEN
        ALTER TABLE ""OrderItems""
        ALTER COLUMN ""LineTotal""
        TYPE numeric(12,2)
        USING (
            CASE
                WHEN ""LineTotal"" IS NULL THEN NULL
                WHEN btrim(""LineTotal"") = '' THEN NULL
                ELSE replace(btrim(""LineTotal""), ',', '.')::numeric
            END
        );
    END IF;

    -- Orders.SubtotalAmount
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema='public'
          AND table_name='Orders'
          AND column_name='SubtotalAmount'
          AND data_type='text'
    ) THEN
        ALTER TABLE ""Orders""
        ALTER COLUMN ""SubtotalAmount""
        TYPE numeric(12,2)
        USING (
            CASE
                WHEN ""SubtotalAmount"" IS NULL THEN NULL
                WHEN btrim(""SubtotalAmount"") = '' THEN NULL
                ELSE replace(btrim(""SubtotalAmount""), ',', '.')::numeric
            END
        );
    END IF;

    -- Orders.TotalAmount
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema='public'
          AND table_name='Orders'
          AND column_name='TotalAmount'
          AND data_type='text'
    ) THEN
        ALTER TABLE ""Orders""
        ALTER COLUMN ""TotalAmount""
        TYPE numeric(12,2)
        USING (
            CASE
                WHEN ""TotalAmount"" IS NULL THEN NULL
                WHEN btrim(""TotalAmount"") = '' THEN NULL
                ELSE replace(btrim(""TotalAmount""), ',', '.')::numeric
            END
        );
    END IF;

    -- Products.UnitPrice
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema='public'
          AND table_name='Products'
          AND column_name='UnitPrice'
          AND data_type='text'
    ) THEN
        ALTER TABLE ""Products""
        ALTER COLUMN ""UnitPrice""
        TYPE numeric(12,2)
        USING (
            CASE
                WHEN ""UnitPrice"" IS NULL THEN NULL
                WHEN btrim(""UnitPrice"") = '' THEN NULL
                ELSE replace(btrim(""UnitPrice""), ',', '.')::numeric
            END
        );
    END IF;

END $$;
");
            }

            // SQLite (local): NO-OP
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // We don't attempt to revert numeric back to text automatically in Postgres.
            // SQLite: NO-OP.
        }
    }
}
