using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The per-position carry-forward tick (27 Sep): intraday by default, held
    /// overnight when ticked; when the tick last changed; and, on a manual-book
    /// row a strategy run carried in at the close, the run position it came from.
    /// </summary>
    public partial class PositionCarryForward : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CarriedFromPositionId",
                table: "paper_positions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CarryForward",
                table: "paper_positions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "CarryForwardChangedUtc",
                table: "paper_positions",
                type: "timestamp with time zone",
                nullable: true);

            // Earlier on 27 Sep every manual-book position was carried: the
            // nightly close stopped squaring the book off. With the tick,
            // unticked means intraday, and the first minute of the new API
            // would square off every position held in a book right now — each
            // opened before Friday's close. Those positions were held overnight
            // on purpose, so they keep being carried until their owner unticks
            // them. Strategy runs are untouched: none holds a position overnight.
            migrationBuilder.Sql(@"
UPDATE paper_positions AS p
SET ""CarryForward"" = TRUE
FROM simulation_runs AS r
WHERE p.""SimulationRunId"" = r.""Id""
  AND r.""StrategyName"" = 'Manual'
  AND p.""Status"" = 'Open';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CarriedFromPositionId",
                table: "paper_positions");

            migrationBuilder.DropColumn(
                name: "CarryForward",
                table: "paper_positions");

            migrationBuilder.DropColumn(
                name: "CarryForwardChangedUtc",
                table: "paper_positions");
        }
    }
}
