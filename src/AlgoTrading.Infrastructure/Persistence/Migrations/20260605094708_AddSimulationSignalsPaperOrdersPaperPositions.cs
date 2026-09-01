using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSimulationSignalsPaperOrdersPaperPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "paper_orders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SimulationRunId = table.Column<long>(type: "bigint", nullable: false),
                    SimulationSignalId = table.Column<long>(type: "bigint", nullable: true),
                    StrategyName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    GroupId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Side = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    OrderType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RequestedPrice = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    FillPrice = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FilledUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paper_orders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "paper_positions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SimulationRunId = table.Column<long>(type: "bigint", nullable: false),
                    StrategyName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    GroupId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Symbol = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    AveragePrice = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    LastMarkPrice = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    RealizedPnl = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    UnrealizedPnl = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OpenedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paper_positions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "simulation_signals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SimulationRunId = table.Column<long>(type: "bigint", nullable: false),
                    StrategyName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SignalType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Symbol = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Price = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    GroupId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MetadataJson = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_simulation_signals", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_paper_orders_GroupId",
                table: "paper_orders",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_paper_orders_SimulationRunId",
                table: "paper_orders",
                column: "SimulationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_paper_orders_SimulationSignalId",
                table: "paper_orders",
                column: "SimulationSignalId");

            migrationBuilder.CreateIndex(
                name: "IX_paper_positions_GroupId",
                table: "paper_positions",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_paper_positions_SimulationRunId",
                table: "paper_positions",
                column: "SimulationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_paper_positions_Status",
                table: "paper_positions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_simulation_signals_SimulationRunId",
                table: "simulation_signals",
                column: "SimulationRunId");

            migrationBuilder.CreateIndex(
                name: "IX_simulation_signals_TimestampUtc",
                table: "simulation_signals",
                column: "TimestampUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "paper_orders");

            migrationBuilder.DropTable(
                name: "paper_positions");

            migrationBuilder.DropTable(
                name: "simulation_signals");
        }
    }
}
