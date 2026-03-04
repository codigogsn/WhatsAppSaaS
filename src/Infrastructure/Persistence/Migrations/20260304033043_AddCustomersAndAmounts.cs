using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomersAndAmounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ✅ QUIRÚRGICO:
            // Esta migración queda SOLO para Customers.
            // NO tocamos Orders/OrderItems amounts ni Products aquí,
            // porque eso ya existe en migraciones previas y en Postgres puede romper por casts/types.

            var isPostgres = ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL";

            if (isPostgres)
            {
                migrationBuilder.CreateTable(
                    name: "Customers",
                    columns: table => new
                    {
                        Id = table.Column<Guid>(type: "uuid", nullable: false),
                        BusinessId = table.Column<Guid>(type: "uuid", nullable: true),
                        PhoneE164 = table.Column<string>(type: "text", nullable: false),
                        Name = table.Column<string>(type: "text", nullable: true),
                        TotalSpent = table.Column<decimal>(type: "numeric(12,2)", nullable: false, defaultValue: 0m),
                        OrdersCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                        FirstSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                        LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                        LastPurchaseAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_Customers", x => x.Id);
                    });

                migrationBuilder.CreateIndex(
                    name: "IX_Customers_PhoneE164",
                    table: "Customers",
                    column: "PhoneE164");
            }
            else
            {
                // SQLite
                migrationBuilder.CreateTable(
                    name: "Customers",
                    columns: table => new
                    {
                        Id = table.Column<Guid>(type: "TEXT", nullable: false),
                        BusinessId = table.Column<Guid>(type: "TEXT", nullable: true),
                        PhoneE164 = table.Column<string>(type: "TEXT", nullable: false),
                        Name = table.Column<string>(type: "TEXT", nullable: true),
                        TotalSpent = table.Column<decimal>(type: "TEXT", nullable: false, defaultValue: 0m),
                        OrdersCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                        FirstSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                        LastSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                        LastPurchaseAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_Customers", x => x.Id);
                    });

                migrationBuilder.CreateIndex(
                    name: "IX_Customers_PhoneE164",
                    table: "Customers",
                    column: "PhoneE164");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Customers");
        }
    }
}
