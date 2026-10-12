using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiPhoneTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_phone_tokens",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    UserName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastUsedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_phone_tokens", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_phone_tokens_TokenHash",
                table: "ai_phone_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ai_phone_tokens_UserId_CreatedUtc",
                table: "ai_phone_tokens",
                columns: new[] { "UserId", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_phone_tokens");
        }
    }
}
