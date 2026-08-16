using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSimulationEquitySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "simulation_equity_snapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SimulationRunId = table.Column<long>(type: "bigint", nullable: false),
                    SnapshotUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InitialCapital = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    UsedCapital = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    AvailableCapital = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    RealizedPnl = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    UnrealizedPnl = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    TotalPnl = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    CurrentEquity = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    OpenPositions = table.Column<int>(type: "integer", nullable: false),
                    ClosedPositions = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_simulation_equity_snapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_simulation_equity_snapshots_SimulationRunId",
                table: "simulation_equity_snapshots",
                column: "SimulationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_simulation_equity_snapshots_SnapshotUtc",
                table: "simulation_equity_snapshots",
                column: "SnapshotUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "simulation_equity_snapshots");
        }
    }
}
