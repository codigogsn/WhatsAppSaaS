using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderCheckoutFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ✅ Fix legacy schema in Postgres (columns created earlier as TEXT)
            // We must CAST using USING, otherwise Postgres refuses the ALTER TYPE.
            // IMPORTANT: Drop FK first, then cast, then recreate FK.

            // 1) Drop FK that depends on Orders.Id / OrderItems.OrderId types
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'FK_OrderItems_Orders_OrderId'
    ) THEN
        ALTER TABLE ""OrderItems"" DROP CONSTRAINT ""FK_OrderItems_Orders_OrderId"";
    END IF;
END $$;
");

            // 2) Cast PK and FK columns to uuid
            migrationBuilder.Sql(@"
ALTER TABLE ""Orders""
  ALTER COLUMN ""Id"" TYPE uuid USING ""Id""::uuid;
");

            migrationBuilder.Sql(@"
ALTER TABLE ""OrderItems""
  ALTER COLUMN ""Id"" TYPE uuid USING ""Id""::uuid;
");

            migrationBuilder.Sql(@"
ALTER TABLE ""OrderItems""
  ALTER COLUMN ""OrderId"" TYPE uuid USING ""OrderId""::uuid;
");

            // 3) Cast CreatedAtUtc from TEXT -> timestamptz safely
            migrationBuilder.Sql(@"
ALTER TABLE ""Orders""
  ALTER COLUMN ""CreatedAtUtc"" TYPE timestamp with time zone
  USING (
    CASE
      WHEN ""CreatedAtUtc"" IS NULL OR ""CreatedAtUtc"" = '' THEN NOW()
      ELSE ""CreatedAtUtc""::timestamp with time zone
    END
  );
");

            // 4) Recreate FK now that types match
            migrationBuilder.Sql(@"
ALTER TABLE ""OrderItems""
  ADD CONSTRAINT ""FK_OrderItems_Orders_OrderId""
  FOREIGN KEY (""OrderId"") REFERENCES ""Orders"" (""Id"")
  ON DELETE CASCADE;
");

            // ✅ New checkout fields
            migrationBuilder.AddColumn<string>(
                name: "AdditionalNotes",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Address",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CheckoutCompleted",
                table: "Orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "CheckoutCompletedAtUtc",
                table: "Orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CheckoutFormSent",
                table: "Orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CustomerIdNumber",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerName",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerPhone",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LocationLat",
                table: "Orders",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LocationLng",
                table: "Orders",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationText",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMethod",
                table: "Orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceiverName",
                table: "Orders",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdditionalNotes",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "Address",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CheckoutCompleted",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CheckoutCompletedAtUtc",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CheckoutFormSent",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CustomerIdNumber",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CustomerName",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CustomerPhone",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "LocationLat",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "LocationLng",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "LocationText",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PaymentMethod",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "ReceiverName",
                table: "Orders");

            // Nota: No revertimos uuid/timestamptz a TEXT en Down.
        }
    }
}
