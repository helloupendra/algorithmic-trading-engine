using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StrategyPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "StrategyPackageId",
                table: "app_user",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "strategy_packages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IncludesAllStrategies = table.Column<bool>(type: "boolean", nullable: false),
                    MaxLotsPerRun = table.Column<int>(type: "integer", nullable: true),
                    MaxConcurrentRuns = table.Column<int>(type: "integer", nullable: true),
                    AllowedUnderlyingsCsv = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AllowLiveMode = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_strategy_packages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "user_strategy_grants",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    StrategyName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    GrantedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    GrantedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_strategy_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_strategy_grants_app_user_UserId",
                        column: x => x.UserId,
                        principalTable: "app_user",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "strategy_package_items",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StrategyPackageId = table.Column<long>(type: "bigint", nullable: false),
                    StrategyName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_strategy_package_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_strategy_package_items_strategy_packages_StrategyPackageId",
                        column: x => x.StrategyPackageId,
                        principalTable: "strategy_packages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_user_StrategyPackageId",
                table: "app_user",
                column: "StrategyPackageId");

            migrationBuilder.CreateIndex(
                name: "IX_strategy_package_items_StrategyPackageId_StrategyName",
                table: "strategy_package_items",
                columns: new[] { "StrategyPackageId", "StrategyName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_strategy_packages_Key",
                table: "strategy_packages",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_strategy_grants_UserId_StrategyName",
                table: "user_strategy_grants",
                columns: new[] { "UserId", "StrategyName" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_app_user_strategy_packages_StrategyPackageId",
                table: "app_user",
                column: "StrategyPackageId",
                principalTable: "strategy_packages",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Traders who existed before packages could run the whole catalog.
            // Dropping them to nothing would lock them out mid-session, so give
            // them a package that says exactly what they already had — and label
            // it so an admin can narrow it deliberately.
            migrationBuilder.Sql("""
                INSERT INTO strategy_packages
                    ("Key", "Name", "Description", "IsEnabled", "IncludesAllStrategies",
                     "MaxLotsPerRun", "MaxConcurrentRuns", "AllowedUnderlyingsCsv",
                     "AllowLiveMode", "CreatedBy", "CreatedUtc", "UpdatedUtc")
                VALUES
                    ('full-access', 'Full access (migrated)',
                     'Every strategy in the catalog, including ones written later. Created when packages were introduced, to preserve what existing traders already had. Narrow or replace it deliberately.',
                     TRUE, TRUE, NULL, NULL, '', FALSE, 'migration', NOW(), NOW())
                ON CONFLICT DO NOTHING;
                """);

            migrationBuilder.Sql("""
                UPDATE app_user
                SET "StrategyPackageId" = (SELECT "Id" FROM strategy_packages WHERE "Key" = 'full-access')
                WHERE "IsActive" AND "Role" = 'Trader' AND "StrategyPackageId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_app_user_strategy_packages_StrategyPackageId",
                table: "app_user");

            migrationBuilder.DropTable(
                name: "strategy_package_items");

            migrationBuilder.DropTable(
                name: "user_strategy_grants");

            migrationBuilder.DropTable(
                name: "strategy_packages");

            migrationBuilder.DropIndex(
                name: "IX_app_user_StrategyPackageId",
                table: "app_user");

            migrationBuilder.DropColumn(
                name: "StrategyPackageId",
                table: "app_user");
        }
    }
}
