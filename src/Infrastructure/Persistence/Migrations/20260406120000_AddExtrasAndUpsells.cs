using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSaaS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExtrasAndUpsells : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    -- Extras table
                    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'Extras') THEN
                        CREATE TABLE "Extras" (
                            "Id" uuid NOT NULL,
                            "BusinessId" uuid NOT NULL,
                            "Name" character varying(200) NOT NULL,
                            "AdditivePrice" numeric(12,2),
                            "IsActive" boolean NOT NULL DEFAULT true,
                            "SortOrder" integer NOT NULL DEFAULT 0,
                            "CreatedAtUtc" timestamp without time zone NOT NULL DEFAULT now(),
                            CONSTRAINT "PK_Extras" PRIMARY KEY ("Id"),
                            CONSTRAINT "FK_Extras_Businesses_BusinessId" FOREIGN KEY ("BusinessId")
                                REFERENCES "Businesses" ("Id") ON DELETE CASCADE
                        );
                        CREATE INDEX "IX_Extras_BusinessId" ON "Extras" ("BusinessId");
                    END IF;

                    -- ExtraMenuItems join table
                    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'ExtraMenuItems') THEN
                        CREATE TABLE "ExtraMenuItems" (
                            "Id" uuid NOT NULL,
                            "ExtraId" uuid NOT NULL,
                            "MenuItemId" uuid NOT NULL,
                            CONSTRAINT "PK_ExtraMenuItems" PRIMARY KEY ("Id"),
                            CONSTRAINT "FK_ExtraMenuItems_Extras_ExtraId" FOREIGN KEY ("ExtraId")
                                REFERENCES "Extras" ("Id") ON DELETE CASCADE,
                            CONSTRAINT "FK_ExtraMenuItems_MenuItems_MenuItemId" FOREIGN KEY ("MenuItemId")
                                REFERENCES "MenuItems" ("Id") ON DELETE CASCADE
                        );
                        CREATE UNIQUE INDEX "IX_ExtraMenuItems_ExtraId_MenuItemId" ON "ExtraMenuItems" ("ExtraId", "MenuItemId");
                    END IF;

                    -- ExtraMenuCategories join table
                    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'ExtraMenuCategories') THEN
                        CREATE TABLE "ExtraMenuCategories" (
                            "Id" uuid NOT NULL,
                            "ExtraId" uuid NOT NULL,
                            "MenuCategoryId" uuid NOT NULL,
                            CONSTRAINT "PK_ExtraMenuCategories" PRIMARY KEY ("Id"),
                            CONSTRAINT "FK_ExtraMenuCategories_Extras_ExtraId" FOREIGN KEY ("ExtraId")
                                REFERENCES "Extras" ("Id") ON DELETE CASCADE,
                            CONSTRAINT "FK_ExtraMenuCategories_MenuCategories_MenuCategoryId" FOREIGN KEY ("MenuCategoryId")
                                REFERENCES "MenuCategories" ("Id") ON DELETE CASCADE
                        );
                        CREATE UNIQUE INDEX "IX_ExtraMenuCategories_ExtraId_MenuCategoryId" ON "ExtraMenuCategories" ("ExtraId", "MenuCategoryId");
                    END IF;

                    -- UpsellRules table
                    IF NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'UpsellRules') THEN
                        CREATE TABLE "UpsellRules" (
                            "Id" uuid NOT NULL,
                            "BusinessId" uuid NOT NULL,
                            "SourceCategoryId" uuid NOT NULL,
                            "SuggestedMenuItemId" uuid,
                            "SuggestionLabel" character varying(200),
                            "CustomMessage" character varying(500),
                            "IsActive" boolean NOT NULL DEFAULT true,
                            "SortOrder" integer NOT NULL DEFAULT 0,
                            "CreatedAtUtc" timestamp without time zone NOT NULL DEFAULT now(),
                            CONSTRAINT "PK_UpsellRules" PRIMARY KEY ("Id"),
                            CONSTRAINT "FK_UpsellRules_Businesses_BusinessId" FOREIGN KEY ("BusinessId")
                                REFERENCES "Businesses" ("Id") ON DELETE CASCADE,
                            CONSTRAINT "FK_UpsellRules_MenuCategories_SourceCategoryId" FOREIGN KEY ("SourceCategoryId")
                                REFERENCES "MenuCategories" ("Id") ON DELETE CASCADE,
                            CONSTRAINT "FK_UpsellRules_MenuItems_SuggestedMenuItemId" FOREIGN KEY ("SuggestedMenuItemId")
                                REFERENCES "MenuItems" ("Id") ON DELETE SET NULL
                        );
                        CREATE INDEX "IX_UpsellRules_BusinessId" ON "UpsellRules" ("BusinessId");
                        CREATE INDEX "IX_UpsellRules_SourceCategoryId" ON "UpsellRules" ("SourceCategoryId");
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TABLE IF EXISTS "ExtraMenuItems";
                DROP TABLE IF EXISTS "ExtraMenuCategories";
                DROP TABLE IF EXISTS "UpsellRules";
                DROP TABLE IF EXISTS "Extras";
                """);
        }
    }
}
