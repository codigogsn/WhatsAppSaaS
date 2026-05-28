using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MemoryLogEnabled",
                table: "Businesses",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "ConversationMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CustomerPhoneE164 = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    WhatsAppMessageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Direction = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Sender = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: true),
                    MediaId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    MimeType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RawPayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    TemplateName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    HandoffMode = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationMessages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessages_BusinessId_ConversationId_ReceivedAtUtc",
                table: "ConversationMessages",
                columns: new[] { "BusinessId", "ConversationId", "ReceivedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessages_BusinessId_CreatedAtUtc",
                table: "ConversationMessages",
                columns: new[] { "BusinessId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessages_BusinessId_WhatsAppMessageId",
                table: "ConversationMessages",
                columns: new[] { "BusinessId", "WhatsAppMessageId" },
                unique: true,
                filter: "\"WhatsAppMessageId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationMessages");

            migrationBuilder.DropColumn(
                name: "MemoryLogEnabled",
                table: "Businesses");
        }
    }
}
