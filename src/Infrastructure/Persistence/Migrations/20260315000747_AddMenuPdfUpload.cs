using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMenuPdfUpload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                // Idempotent: startup legacy SQL may have already created these
                migrationBuilder.Sql(
                    """ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "MenuPdfUrl" varchar(500)""");
                migrationBuilder.Sql(
                    """CREATE TABLE IF NOT EXISTS "MenuPdfs" ("Id" uuid NOT NULL PRIMARY KEY, "BusinessId" uuid NOT NULL, "Data" bytea NOT NULL, "ContentType" varchar(100) NOT NULL, "UploadedAtUtc" timestamp NOT NULL)""");
                migrationBuilder.Sql(
                    """CREATE UNIQUE INDEX IF NOT EXISTS "IX_MenuPdfs_BusinessId" ON "MenuPdfs" ("BusinessId")""");
            }
            else
            {
                // SQLite (tests): standard EF operations on fresh DB
                migrationBuilder.AddColumn<string>(
                    name: "MenuPdfUrl",
                    table: "Businesses",
                    type: "TEXT",
                    maxLength: 500,
                    nullable: true);

                migrationBuilder.CreateTable(
                    name: "MenuPdfs",
                    columns: table => new
                    {
                        Id = table.Column<Guid>(type: "TEXT", nullable: false),
                        BusinessId = table.Column<Guid>(type: "TEXT", nullable: false),
                        Data = table.Column<byte[]>(type: "BLOB", nullable: false),
                        ContentType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                        UploadedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                    },
                    constraints: table =>
                    {
                        table.PrimaryKey("PK_MenuPdfs", x => x.Id);
                    });

                migrationBuilder.CreateIndex(
                    name: "IX_MenuPdfs_BusinessId",
                    table: "MenuPdfs",
                    column: "BusinessId",
                    unique: true);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MenuPdfs");

            migrationBuilder.DropColumn(
                name: "MenuPdfUrl",
                table: "Businesses");
        }
    }
}
