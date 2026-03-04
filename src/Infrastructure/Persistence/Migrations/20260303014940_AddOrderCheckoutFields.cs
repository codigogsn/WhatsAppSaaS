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
            // ✅ Fix legacy schema ONLY in Postgres (cuando columnas quedaron como TEXT/VARCHAR)
            // Importante: NO tocamos ni recreamos FK aquí (eso fue lo que explotó en Render).
            if (ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql(@"
DO $$
BEGIN
    -- Orders.Id -> uuid (solo si hoy está como text/varchar)
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'Orders'
          AND column_name = 'Id'
          AND data_type IN ('text', 'character varying')
    ) THEN
        ALTER TABLE ""Orders""
          ALTER COLUMN ""Id"" TYPE uuid USING ""Id""::uuid;
    END IF;

    -- OrderItems.Id -> uuid (solo si hoy está como text/varchar)
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'OrderItems'
          AND column_name = 'Id'
          AND data_type IN ('text', 'character varying')
    ) THEN
        ALTER TABLE ""OrderItems""
          ALTER COLUMN ""Id"" TYPE uuid USING ""Id""::uuid;
    END IF;

    -- OrderItems.OrderId -> uuid (solo si hoy está como text/varchar)
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'OrderItems'
          AND column_name = 'OrderId'
          AND data_type IN ('text', 'character varying')
    ) THEN
        ALTER TABLE ""OrderItems""
          ALTER COLUMN ""OrderId"" TYPE uuid USING ""OrderId""::uuid;
    END IF;

    -- Orders.CreatedAtUtc -> timestamptz (solo si hoy está como text/varchar)
    IF EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = 'public'
          AND table_name = 'Orders'
          AND column_name = 'CreatedAtUtc'
          AND data_type IN ('text', 'character varying')
    ) THEN
        ALTER TABLE ""Orders""
          ALTER COLUMN ""CreatedAtUtc"" TYPE timestamp with time zone
          USING (
            CASE
              WHEN ""CreatedAtUtc"" IS NULL OR ""CreatedAtUtc"" = '' THEN NOW()
              ELSE ""CreatedAtUtc""::timestamp with time zone
            END
          );
    END IF;

EXCEPTION WHEN OTHERS THEN
    RAISE NOTICE 'Skip legacy schema fix in AddOrderCheckoutFields: %', SQLERRM;
END $$;
");
            }

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

            // Nota: no revertimos uuid/timestamptz a TEXT en Down.
        }
    }
}
