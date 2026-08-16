using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveTicksAndBars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "live_bars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Resolution = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BarStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Open = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    High = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    Low = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    Close = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    VolumeDelta = table.Column<long>(type: "bigint", nullable: false),
                    TickCount = table.Column<int>(type: "integer", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_live_bars", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "live_ticks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DataType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExchangeTimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastTradedPrice = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    BidPrice = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    AskPrice = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    BidSize = table.Column<long>(type: "bigint", nullable: true),
                    AskSize = table.Column<long>(type: "bigint", nullable: true),
                    Open = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    High = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    Low = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    PrevClose = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    Volume = table.Column<long>(type: "bigint", nullable: true),
                    RawPayload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_live_ticks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_live_bars_Symbol_Resolution_BarStartUtc",
                table: "live_bars",
                columns: new[] { "Symbol", "Resolution", "BarStartUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_live_ticks_Symbol_ReceivedUtc",
                table: "live_ticks",
                columns: new[] { "Symbol", "ReceivedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "live_bars");

            migrationBuilder.DropTable(
                name: "live_ticks");
        }
    }
}
