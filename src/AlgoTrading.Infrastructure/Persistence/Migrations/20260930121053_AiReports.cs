using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_reports",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AgentKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubjectType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SubjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SessionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CallId = table.Column<long>(type: "bigint", nullable: true),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, defaultValue: ""),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false, defaultValue: ""),
                    Body = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    DataJson = table.Column<string>(type: "text", nullable: false, defaultValue: "{}"),
                    Error = table.Column<string>(type: "text", nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_reports", x => x.Id);
                    table.CheckConstraint("CK_ai_reports_Status", "\"Status\" IN ('ok', 'invalid', 'failed')");
                    table.CheckConstraint("CK_ai_reports_SubjectType", "\"SubjectType\" IN ('run', 'news', 'filing', 'incident')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_reports_AgentKey_Id",
                table: "ai_reports",
                columns: new[] { "AgentKey", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_reports_SessionDate",
                table: "ai_reports",
                column: "SessionDate");

            migrationBuilder.CreateIndex(
                name: "UX_ai_reports_Agent_Subject",
                table: "ai_reports",
                columns: new[] { "AgentKey", "SubjectType", "SubjectId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_reports");
        }
    }
}
