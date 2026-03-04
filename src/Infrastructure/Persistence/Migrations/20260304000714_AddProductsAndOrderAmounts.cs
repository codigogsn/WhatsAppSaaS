using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    public partial class AddProductsAndOrderAmounts : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ✅ QUIRÚRGICO:
            // Esta migración fue generada bajo SQLite (bool -> INTEGER),
            // y en Postgres eso revienta. Por eso:
            // - NO tocamos columnas existentes (CheckoutFormSent / CheckoutCompleted, etc.)
            // - Solo creamos Products y agregamos columnas nuevas para montos/precios.

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Name = table.Column<string>(nullable: false),
                    UnitPrice = table.Column<decimal>(nullable: false),
                    IsActive = table.Column<bool>(nullable: false, defaultValue: true),
                    CreatedAtUtc = table.Column<DateTime>(nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                });

            migrationBuilder.AddColumn<decimal>(
                name: "UnitPrice",
                table: "OrderItems",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LineTotal",
                table: "OrderItems",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SubtotalAmount",
                table: "Orders",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalAmount",
                table: "Orders",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UnitPrice",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "LineTotal",
                table: "OrderItems");

            migrationBuilder.DropColumn(
                name: "SubtotalAmount",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TotalAmount",
                table: "Orders");

            migrationBuilder.DropTable(
                name: "Products");
        }
    }
}
