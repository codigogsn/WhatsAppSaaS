using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenVersionToBusinessUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "Orders",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);

            migrationBuilder.AddColumn<int>(
                name: "TokenVersion",
                table: "BusinessUsers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "TokenVersion",
                table: "BusinessUsers");
        }
    }
}
