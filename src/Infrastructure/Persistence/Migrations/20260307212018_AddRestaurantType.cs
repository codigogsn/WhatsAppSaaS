using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRestaurantType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                // Postgres: idempotent guard — legacy repair may have already added this column
                migrationBuilder.Sql("""
                    DO $$ BEGIN
                        IF NOT EXISTS (
                            SELECT 1 FROM information_schema.columns
                            WHERE table_name = 'Businesses' AND column_name = 'RestaurantType'
                        ) THEN
                            ALTER TABLE "Businesses" ADD COLUMN "RestaurantType" character varying(50);
                        END IF;
                    END $$;
                """);
            }
            else
            {
                // SQLite: simple AddColumn (no IF NOT EXISTS support)
                migrationBuilder.AddColumn<string>(
                    name: "RestaurantType",
                    table: "Businesses",
                    type: "TEXT",
                    maxLength: 50,
                    nullable: true);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RestaurantType",
                table: "Businesses");
        }
    }
}
