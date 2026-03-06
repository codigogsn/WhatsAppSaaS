using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessPaymentMobileFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PaymentMobileBank",
                table: "Businesses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMobileId",
                table: "Businesses",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMobilePhone",
                table: "Businesses",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PaymentMobileBank",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "PaymentMobileId",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "PaymentMobilePhone",
                table: "Businesses");
        }
    }
}
