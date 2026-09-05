using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserModuleGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxConcurrentRuns",
                table: "app_user",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "user_module_grants",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ModuleKey = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    GrantedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    GrantedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_module_grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_module_grants_app_user_UserId",
                        column: x => x.UserId,
                        principalTable: "app_user",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Grants are deny-by-default, but the accounts that already exist had
            // full access until this migration ran. Taking it away silently would
            // lock working traders out mid-session, so preserve what they had and
            // let an admin narrow it deliberately.
            migrationBuilder.Sql("""
                INSERT INTO user_module_grants ("UserId", "ModuleKey", "GrantedBy", "GrantedUtc")
                SELECT u."Id", m.key, 'migration', NOW()
                FROM app_user u
                CROSS JOIN (VALUES ('strategies'), ('backtesting'), ('market-data')) AS m(key)
                WHERE u."IsActive" AND u."Role" = 'Trader'
                ON CONFLICT DO NOTHING;
                """);

            // The engine's account is a machine, not a trader. Left in the Trader
            // role it shows up in the traders list and looks like a person.
            migrationBuilder.Sql(
                "UPDATE app_user SET \"Role\" = 'Service' WHERE \"UserName\" = 'engine-service';");

            migrationBuilder.CreateIndex(
                name: "IX_user_module_grants_UserId_ModuleKey",
                table: "user_module_grants",
                columns: new[] { "UserId", "ModuleKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_module_grants");

            migrationBuilder.DropColumn(
                name: "MaxConcurrentRuns",
                table: "app_user");
        }
    }
}
