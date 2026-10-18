using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PortfolioConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "portfolio_connections",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Broker = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AccountLabel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TokenProtected = table.Column<string>(type: "text", nullable: false),
                    ConnectedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_portfolio_connections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "portfolio_snapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Broker = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TakenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    HoldingsJson = table.Column<string>(type: "text", nullable: false),
                    PositionsJson = table.Column<string>(type: "text", nullable: false),
                    FundsJson = table.Column<string>(type: "text", nullable: false),
                    SummaryKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SummaryJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_portfolio_snapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_portfolio_connections_UserId_Broker",
                table: "portfolio_connections",
                columns: new[] { "UserId", "Broker" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_portfolio_snapshots_TakenUtc",
                table: "portfolio_snapshots",
                column: "TakenUtc");

            migrationBuilder.CreateIndex(
                name: "IX_portfolio_snapshots_UserId_Broker_TakenUtc",
                table: "portfolio_snapshots",
                columns: new[] { "UserId", "Broker", "TakenUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "portfolio_connections");

            migrationBuilder.DropTable(
                name: "portfolio_snapshots");
        }
    }
}
