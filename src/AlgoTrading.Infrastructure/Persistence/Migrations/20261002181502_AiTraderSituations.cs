using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiTraderSituations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_trader_situations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Underlying = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Slot = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    BuiltUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    MovePrevClosePct = table.Column<double>(type: "double precision", nullable: false),
                    MoveOpenPct = table.Column<double>(type: "double precision", nullable: false),
                    Last30MinPct = table.Column<double>(type: "double precision", nullable: false),
                    RangePct = table.Column<double>(type: "double precision", nullable: false),
                    EmaSide = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    EmaGapPct = table.Column<double>(type: "double precision", nullable: false),
                    Vix = table.Column<double>(type: "double precision", nullable: true),
                    VixChangePct = table.Column<double>(type: "double precision", nullable: true),
                    MinutesSinceOpen = table.Column<int>(type: "integer", nullable: false),
                    DaysToExpiry = table.Column<int>(type: "integer", nullable: true),
                    Weekday = table.Column<int>(type: "integer", nullable: false),
                    Return30MinPct = table.Column<double>(type: "double precision", nullable: true),
                    Return60MinPct = table.Column<double>(type: "double precision", nullable: true),
                    ReturnToClosePct = table.Column<double>(type: "double precision", nullable: true),
                    MaxUpPct = table.Column<double>(type: "double precision", nullable: true),
                    MaxDownPct = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_trader_situations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_trader_situations_Underlying_Day_Slot",
                table: "ai_trader_situations",
                columns: new[] { "Underlying", "Day", "Slot" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_trader_situations");
        }
    }
}
