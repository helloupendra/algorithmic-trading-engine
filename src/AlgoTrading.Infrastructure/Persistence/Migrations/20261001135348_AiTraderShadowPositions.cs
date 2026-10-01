using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AiTraderShadowPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_trader_shadow_positions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DecisionId = table.Column<long>(type: "bigint", nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReplaySessionId = table.Column<long>(type: "bigint", nullable: true),
                    Day = table.Column<DateOnly>(type: "date", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Underlying = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OptionType = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Strike = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    Expiry = table.Column<DateOnly>(type: "date", nullable: false),
                    Lots = table.Column<int>(type: "integer", nullable: false),
                    LotSize = table.Column<int>(type: "integer", nullable: false),
                    EntryUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    StopLoss = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    Target = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    MarkPrice = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    MarkUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExitUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExitPrice = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    ExitReason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: ""),
                    Charges = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    NetPnl = table.Column<decimal>(type: "numeric(18,6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_trader_shadow_positions", x => x.Id);
                    table.CheckConstraint("CK_ai_trader_shadow_positions_Mode", "\"Mode\" IN ('shadow', 'replay')");
                    table.CheckConstraint("CK_ai_trader_shadow_positions_OptionType", "\"OptionType\" IN ('CE', 'PE')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_trader_shadow_positions_Day_Replay",
                table: "ai_trader_shadow_positions",
                columns: new[] { "Day", "ReplaySessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ai_trader_shadow_positions_Decision",
                table: "ai_trader_shadow_positions",
                column: "DecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_ai_trader_shadow_positions_Open",
                table: "ai_trader_shadow_positions",
                column: "ExitUtc",
                filter: "\"ExitUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_trader_shadow_positions");
        }
    }
}
