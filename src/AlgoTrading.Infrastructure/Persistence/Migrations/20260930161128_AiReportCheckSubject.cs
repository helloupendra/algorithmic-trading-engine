using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiReportCheckSubject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ai_reports_SubjectType",
                table: "ai_reports");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ai_reports_SubjectType",
                table: "ai_reports",
                sql: "\"SubjectType\" IN ('run', 'news', 'filing', 'incident', 'check')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ai_reports_SubjectType",
                table: "ai_reports");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ai_reports_SubjectType",
                table: "ai_reports",
                sql: "\"SubjectType\" IN ('run', 'news', 'filing', 'incident')");
        }
    }
}
