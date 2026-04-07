using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDashboardLayouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'DashboardLayouts') THEN
                        CREATE TABLE "DashboardLayouts" (
                            "Id" uuid NOT NULL,
                            "BusinessId" uuid NOT NULL,
                            "LayoutJson" text NOT NULL,
                            "UpdatedAtUtc" timestamp without time zone NOT NULL DEFAULT now(),
                            CONSTRAINT "PK_DashboardLayouts" PRIMARY KEY ("Id")
                        );
                        CREATE UNIQUE INDEX "IX_DashboardLayouts_BusinessId" ON "DashboardLayouts" ("BusinessId");
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TABLE IF EXISTS "DashboardLayouts";""");
        }
    }
}
