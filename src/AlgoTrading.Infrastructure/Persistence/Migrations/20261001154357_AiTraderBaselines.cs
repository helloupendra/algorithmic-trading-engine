using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiTraderBaselines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_trader_baselines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ComputedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Rule = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Underlying = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OptionType = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false, defaultValue: ""),
                    Symbol = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: ""),
                    Lots = table.Column<int>(type: "integer", nullable: false),
                    LotSize = table.Column<int>(type: "integer", nullable: false),
                    EntryUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EntryPrice = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    StopLoss = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    Target = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    ExitUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExitPrice = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    ExitReason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: ""),
                    Charges = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    NetPnl = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_trader_baselines", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_trader_baselines_Day_Rule",
                table: "ai_trader_baselines",
                columns: new[] { "Day", "Rule" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_trader_baselines");
        }
    }
}
