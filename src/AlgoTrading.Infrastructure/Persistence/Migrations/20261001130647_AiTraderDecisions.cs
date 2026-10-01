using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiTraderDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_trader_decisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClockUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReplaySessionId = table.Column<long>(type: "bigint", nullable: true),
                    BriefHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Brief = table.Column<string>(type: "text", nullable: false),
                    CallId = table.Column<long>(type: "bigint", nullable: true),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false, defaultValue: ""),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: ""),
                    Underlying = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: ""),
                    PlanJson = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false, defaultValue: ""),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    Allowed = table.Column<bool>(type: "boolean", nullable: false),
                    Rule = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: ""),
                    Why = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false, defaultValue: ""),
                    Executed = table.Column<bool>(type: "boolean", nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: false),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_trader_decisions", x => x.Id);
                    table.CheckConstraint("CK_ai_trader_decisions_Mode", "\"Mode\" IN ('shadow', 'live', 'replay')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_trader_decisions_Day_Clock",
                table: "ai_trader_decisions",
                columns: new[] { "Day", "ClockUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_trader_decisions_ReplaySession",
                table: "ai_trader_decisions",
                column: "ReplaySessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_trader_decisions");
        }
    }
}
