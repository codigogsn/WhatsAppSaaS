using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddVerticalType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql("""
                    DO $$ BEGIN
                        IF NOT EXISTS (
                            SELECT 1 FROM information_schema.columns
                            WHERE table_name = 'Businesses' AND column_name = 'VerticalType'
                        ) THEN
                            ALTER TABLE "Businesses" ADD COLUMN "VerticalType" varchar(30) DEFAULT 'restaurant' NOT NULL;
                        END IF;
                    END $$;
                    """);
            }
            else
            {
                migrationBuilder.AddColumn<string>(
                    name: "VerticalType",
                    table: "Businesses",
                    type: "TEXT",
                    maxLength: 30,
                    nullable: false,
                    defaultValue: "restaurant");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VerticalType",
                table: "Businesses");
        }
    }
}
