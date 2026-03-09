using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExchangeRatesAndCurrencyRef : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                // PostgreSQL: idempotent SQL for production (schema may already be partially applied)
                migrationBuilder.Sql("""
                    DO $$ BEGIN
                        IF NOT EXISTS (
                            SELECT 1 FROM information_schema.columns
                            WHERE table_name = 'Businesses' AND column_name = 'CurrencyReference'
                        ) THEN
                            ALTER TABLE "Businesses" ADD COLUMN "CurrencyReference" varchar(20);
                        END IF;
                    END $$;
                    """);

                migrationBuilder.Sql("""
                    CREATE TABLE IF NOT EXISTS "ExchangeRates" (
                        "Id" uuid NOT NULL PRIMARY KEY,
                        "RateDate" timestamp NOT NULL,
                        "UsdRate" numeric(12,2) NOT NULL,
                        "EurRate" numeric(12,2) NOT NULL,
                        "Source" varchar(50) NOT NULL DEFAULT 'bcv',
                        "FetchedAtUtc" timestamp NOT NULL DEFAULT now()
                    );
                    """);

                migrationBuilder.Sql("""
                    CREATE UNIQUE INDEX IF NOT EXISTS "IX_ExchangeRates_RateDate"
                    ON "ExchangeRates" ("RateDate");
                    """);
            }
            else
            {
                // SQLite (tests/dev): standard EF operations
                migrationBuilder.AddColumn<string>(
                    name: "CurrencyReference",
                    table: "Businesses",
                    type: "TEXT",
                    maxLength: 20,
                    nullable: true);

                migrationBuilder.CreateTable(
                    name: "ExchangeRates",
                    columns: table => new
                    {
                        Id = table.Column<Guid>(type: "TEXT", nullable: false),
                        RateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                        UsdRate = table.Column<decimal>(type: "TEXT", precision: 12, scale: 2, nullable: false),
                        EurRate = table.Column<decimal>(type: "TEXT", precision: 12, scale: 2, nullable: false),
                        Source = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                        FetchedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_ExchangeRates", x => x.Id);
                    });

                migrationBuilder.CreateIndex(
                    name: "IX_ExchangeRates_RateDate",
                    table: "ExchangeRates",
                    column: "RateDate",
                    unique: true);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExchangeRates");

            migrationBuilder.DropColumn(
                name: "CurrencyReference",
                table: "Businesses");
        }
    }
}
