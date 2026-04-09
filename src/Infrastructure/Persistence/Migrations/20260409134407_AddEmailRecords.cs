using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromEmail = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: true),
                    SuggestedReply = table.Column<string>(type: "TEXT", nullable: true),
                    GmailMessageId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UpsellRules_SuggestedMenuItemId",
                table: "UpsellRules",
                column: "SuggestedMenuItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ExtraMenuItems_MenuItemId",
                table: "ExtraMenuItems",
                column: "MenuItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ExtraMenuCategories_MenuCategoryId",
                table: "ExtraMenuCategories",
                column: "MenuCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailRecords_BusinessId",
                table: "EmailRecords",
                column: "BusinessId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailRecords_BusinessId_GmailMessageId",
                table: "EmailRecords",
                columns: new[] { "BusinessId", "GmailMessageId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailRecords");

            migrationBuilder.DropIndex(
                name: "IX_UpsellRules_SuggestedMenuItemId",
                table: "UpsellRules");

            migrationBuilder.DropIndex(
                name: "IX_ExtraMenuItems_MenuItemId",
                table: "ExtraMenuItems");

            migrationBuilder.DropIndex(
                name: "IX_ExtraMenuCategories_MenuCategoryId",
                table: "ExtraMenuCategories");
        }
    }
}
