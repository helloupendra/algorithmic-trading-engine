using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserInvites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_invites",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TokenHash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SuggestedUserName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ModuleKeysCsv = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    StrategyPackageId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AcceptedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcceptedUserId = table.Column<long>(type: "bigint", nullable: true),
                    RevokedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_invites", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_invites_Email",
                table: "user_invites",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_user_invites_TokenHash",
                table: "user_invites",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_invites");
        }
    }
}
