using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PnlSeriesFillPricingSignalIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientSignalId",
                table: "simulation_signals",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MetadataJson",
                table: "paper_orders",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "run_pnl_minutes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SimulationRunId = table.Column<long>(type: "bigint", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Realized = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Unrealized = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Charges = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Net = table.Column<decimal>(type: "numeric(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_run_pnl_minutes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_simulation_signals_SimulationRunId_ClientSignalId",
                table: "simulation_signals",
                columns: new[] { "SimulationRunId", "ClientSignalId" },
                unique: true,
                filter: "\"ClientSignalId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_run_pnl_minutes_AtUtc",
                table: "run_pnl_minutes",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "UX_run_pnl_minutes_SimulationRunId_AtUtc",
                table: "run_pnl_minutes",
                columns: new[] { "SimulationRunId", "AtUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "run_pnl_minutes");

            migrationBuilder.DropIndex(
                name: "IX_simulation_signals_SimulationRunId_ClientSignalId",
                table: "simulation_signals");

            migrationBuilder.DropColumn(
                name: "ClientSignalId",
                table: "simulation_signals");

            migrationBuilder.DropColumn(
                name: "MetadataJson",
                table: "paper_orders");
        }
    }
}
