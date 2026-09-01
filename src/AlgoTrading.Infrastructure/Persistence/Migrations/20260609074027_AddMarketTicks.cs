using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketTicks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "market_ticks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DataType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExchangeTimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastTradedPrice = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    BidPrice = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    AskPrice = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    BidSize = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    AskSize = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    Open = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    High = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    Low = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    PrevClose = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    Volume = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    RawPayload = table.Column<string>(type: "text", nullable: false),
                    ReceivedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_market_ticks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_market_ticks_ExchangeTimestampUtc",
                table: "market_ticks",
                column: "ExchangeTimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_market_ticks_ReceivedUtc",
                table: "market_ticks",
                column: "ReceivedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_market_ticks_Symbol_ExchangeTimestampUtc",
                table: "market_ticks",
                columns: new[] { "Symbol", "ExchangeTimestampUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "market_ticks");
        }
    }
}
