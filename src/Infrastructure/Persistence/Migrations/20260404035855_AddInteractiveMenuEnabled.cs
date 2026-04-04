using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInteractiveMenuEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_name = 'Businesses'
                          AND column_name = 'InteractiveMenuEnabled'
                    ) THEN
                        ALTER TABLE "Businesses"
                            ADD COLUMN "InteractiveMenuEnabled" boolean NOT NULL DEFAULT false;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_name = 'Businesses'
                          AND column_name = 'InteractiveMenuEnabled'
                    ) THEN
                        ALTER TABLE "Businesses" DROP COLUMN "InteractiveMenuEnabled";
                    END IF;
                END $$;
                """);
        }
    }
}
