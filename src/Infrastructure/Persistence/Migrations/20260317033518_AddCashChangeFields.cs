using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCashChangeFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CashBcvRateUsed",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CashChangeAmount",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CashChangeAmountBs",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CashChangeRequired",
                table: "Orders",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CashChangeReturned",
                table: "Orders",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "CashChangeReturnedAtUtc",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CashChangeReturnedBy",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CashChangeReturnedReference",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CashCurrency",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CashPayoutBank",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CashPayoutIdNumber",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CashPayoutPhone",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CashTenderedAmount",
                table: "Orders",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CashBcvRateUsed",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CashChangeAmount",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CashChangeAmountBs",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CashChangeRequired",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CashChangeReturned",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CashChangeReturnedAtUtc",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CashChangeReturnedBy",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CashChangeReturnedReference",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CashCurrency",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CashPayoutBank",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CashPayoutIdNumber",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CashPayoutPhone",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "CashTenderedAmount",
                table: "Orders");
        }
    }
}
