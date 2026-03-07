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
            // Use IF NOT EXISTS for Postgres to avoid collision with legacy schema repair.
            // SQLite doesn't support IF NOT EXISTS on ADD COLUMN, but SQLite
            // won't have the legacy repair path so the standard API is safe there.
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", System.StringComparison.OrdinalIgnoreCase) == true)
            {
                migrationBuilder.Sql("""ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "Address" text""");
                migrationBuilder.Sql("""ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "Greeting" text""");
                migrationBuilder.Sql("""ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "LogoUrl" text""");
                migrationBuilder.Sql("""ALTER TABLE "Businesses" ADD COLUMN IF NOT EXISTS "Schedule" text""");
            }
            else
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
