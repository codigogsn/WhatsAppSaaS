using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    public partial class LinkOrdersToCustomers : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ✅ QUIRÚRGICO:
            // Solo agregamos la relación Orders -> Customers.
            // Sin tocar tipos/columnas existentes (para no romper Postgres).

            if (ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.AddColumn<Guid>(
                    name: "CustomerId",
                    table: "Orders",
                    type: "uuid",
                    nullable: true);

                migrationBuilder.CreateIndex(
                    name: "IX_Orders_CustomerId",
                    table: "Orders",
                    column: "CustomerId");

                migrationBuilder.AddForeignKey(
                    name: "FK_Orders_Customers_CustomerId",
                    table: "Orders",
                    column: "CustomerId",
                    principalTable: "Customers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            }
            else
            {
                // SQLite / otros providers
                migrationBuilder.AddColumn<Guid>(
                    name: "CustomerId",
                    table: "Orders",
                    type: "TEXT",
                    nullable: true);

                migrationBuilder.CreateIndex(
                    name: "IX_Orders_CustomerId",
                    table: "Orders",
                    column: "CustomerId");

                migrationBuilder.AddForeignKey(
                    name: "FK_Orders_Customers_CustomerId",
                    table: "Orders",
                    column: "CustomerId",
                    principalTable: "Customers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            }
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Orders_Customers_CustomerId",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_Orders_CustomerId",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "Orders");
        }
    }
}
