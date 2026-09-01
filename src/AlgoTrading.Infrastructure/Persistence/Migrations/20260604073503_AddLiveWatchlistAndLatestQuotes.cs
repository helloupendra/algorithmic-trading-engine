using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveWatchlistAndLatestQuotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "live_quotes_latest",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DataType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    LastTradedPrice = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    Open = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    High = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    Low = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    Close = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    Volume = table.Column<long>(type: "bigint", nullable: true),
                    RawPayload = table.Column<string>(type: "text", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_live_quotes_latest", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "live_watchlist",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Symbol = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DataType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_live_watchlist", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_live_quotes_latest_Symbol",
                table: "live_quotes_latest",
                column: "Symbol",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_live_watchlist_Symbol",
                table: "live_watchlist",
                column: "Symbol",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "live_quotes_latest");

            migrationBuilder.DropTable(
                name: "live_watchlist");
        }
    }
}
