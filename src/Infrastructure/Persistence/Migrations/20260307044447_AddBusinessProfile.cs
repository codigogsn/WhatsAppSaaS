using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Address",
                table: "Businesses",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Greeting",
                table: "Businesses",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogoUrl",
                table: "Businesses",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Schedule",
                table: "Businesses",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Address",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "Greeting",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "LogoUrl",
                table: "Businesses");

            migrationBuilder.DropColumn(
                name: "Schedule",
                table: "Businesses");
        }
    }
}
