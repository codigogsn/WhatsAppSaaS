using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Comprehensive column-type fix for PostgreSQL production schema.
    /// All SQLite-generated migrations created decimal columns as TEXT and boolean
    /// columns as INTEGER. This migration converts them to native PostgreSQL types.
    /// Idempotent: each ALTER is guarded by an information_schema check.
    /// No-op on SQLite.
    /// </summary>
    public partial class FixCashColumnsForPostgres : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
                return;

            // Helper SQL: convert a column from text to numeric, safely handling NULL and empty strings
            static string TextToNumeric(string table, string column) => $"""
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = '{table}' AND column_name = '{column}' AND data_type = 'text'
                ) THEN
                    ALTER TABLE "{table}" ALTER COLUMN "{column}" TYPE numeric
                    USING CASE WHEN btrim("{column}") = '' OR "{column}" IS NULL THEN NULL ELSE "{column}"::numeric END;
                END IF;
            """;

            // Helper SQL: convert a column from integer/text to boolean
            static string IntToBool(string table, string column) => $"""
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_name = '{table}' AND column_name = '{column}' AND data_type <> 'boolean'
                ) THEN
                    ALTER TABLE "{table}" ALTER COLUMN "{column}" TYPE boolean
                    USING CASE
                        WHEN "{column}" IS NULL THEN false
                        WHEN "{column}"::text IN ('1','true','t','True','TRUE') THEN true
                        ELSE false
                    END;
                END IF;
            """;

            migrationBuilder.Sql($"""
                DO $$
                BEGIN
                    -- ═══ Orders: decimal columns stored as text ═══
                    {TextToNumeric("Orders", "SubtotalAmount")}
                    {TextToNumeric("Orders", "TotalAmount")}
                    {TextToNumeric("Orders", "DeliveryFee")}
                    {TextToNumeric("Orders", "LocationLat")}
                    {TextToNumeric("Orders", "LocationLng")}

                    -- ═══ Orders: cash-change decimal columns stored as text ═══
                    {TextToNumeric("Orders", "CashTenderedAmount")}
                    {TextToNumeric("Orders", "CashBcvRateUsed")}
                    {TextToNumeric("Orders", "CashChangeAmount")}
                    {TextToNumeric("Orders", "CashChangeAmountBs")}

                    -- ═══ Orders: cash-change boolean columns stored as integer ═══
                    {IntToBool("Orders", "CashChangeRequired")}
                    {IntToBool("Orders", "CashChangeReturned")}

                    -- ═══ OrderItems: decimal columns stored as text ═══
                    {TextToNumeric("OrderItems", "UnitPrice")}
                    {TextToNumeric("OrderItems", "LineTotal")}

                    -- ═══ MenuItems: price stored as text ═══
                    {TextToNumeric("MenuItems", "Price")}

                    -- ═══ Customers: TotalSpent stored as text ═══
                    {TextToNumeric("Customers", "TotalSpent")}

                    -- ═══ Products: Price stored as text (legacy table) ═══
                    {TextToNumeric("Products", "Price")}
                END $$;
            """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — reverting types would re-introduce the bug.
        }
    }
}
