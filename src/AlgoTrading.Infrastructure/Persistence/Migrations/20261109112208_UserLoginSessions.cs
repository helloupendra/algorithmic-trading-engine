using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserLoginSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Old credentials cannot be bound reliably to a login family. Require a fresh login on rollout.
            migrationBuilder.Sql("UPDATE app_user SET \"TokensValidFromUtc\" = GREATEST(COALESCE(\"TokensValidFromUtc\", '-infinity'::timestamptz), NOW())");
            migrationBuilder.Sql("UPDATE user_refresh_tokens SET \"RevokedUtc\" = COALESCE(\"RevokedUtc\", NOW())");
            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "user_refresh_tokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "user_sessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_sessions_app_user_UserId",
                        column: x => x.UserId,
                        principalTable: "app_user",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_refresh_tokens_SessionId",
                table: "user_refresh_tokens",
                column: "SessionId",
                unique: true,
                filter: "\"RevokedUtc\" IS NULL AND \"SessionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_user_sessions_UserId_RevokedUtc",
                table: "user_sessions",
                columns: new[] { "UserId", "RevokedUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_user_refresh_tokens_user_sessions_SessionId",
                table: "user_refresh_tokens",
                column: "SessionId",
                principalTable: "user_sessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A rollback must not resurrect a family the newer API revoked.
            migrationBuilder.Sql("UPDATE app_user SET \"TokensValidFromUtc\" = GREATEST(COALESCE(\"TokensValidFromUtc\", '-infinity'::timestamptz), NOW())");
            migrationBuilder.Sql("UPDATE user_refresh_tokens SET \"RevokedUtc\" = COALESCE(\"RevokedUtc\", NOW())");
            migrationBuilder.DropForeignKey(
                name: "FK_user_refresh_tokens_user_sessions_SessionId",
                table: "user_refresh_tokens");

            migrationBuilder.DropTable(
                name: "user_sessions");

            migrationBuilder.DropIndex(
                name: "IX_user_refresh_tokens_SessionId",
                table: "user_refresh_tokens");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "user_refresh_tokens");
        }
    }
}
