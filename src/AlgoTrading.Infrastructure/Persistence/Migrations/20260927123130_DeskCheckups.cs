using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeskCheckups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "desk_checkups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Slot = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RequestedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RequestedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: ""),
                    StartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Verdict = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: ""),
                    Headline = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    ItemsJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    Error = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    NotifiedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Host = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_desk_checkups", x => x.Id);
                    table.CheckConstraint("CK_desk_checkups_Status", "\"Status\" IN ('requested', 'running', 'done', 'failed')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_desk_checkups_CompletedUtc",
                table: "desk_checkups",
                column: "CompletedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_desk_checkups_Status",
                table: "desk_checkups",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "desk_checkups");
        }
    }
}
