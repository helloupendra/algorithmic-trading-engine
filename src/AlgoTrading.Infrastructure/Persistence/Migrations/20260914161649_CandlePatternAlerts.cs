using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CandlePatternAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DedupeKey",
                table: "alert_events",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "candle_pattern_rules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SymbolsCsv = table.Column<string>(type: "text", nullable: false),
                    GroupsCsv = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TimeframesCsv = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PatternsCsv = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Notify = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candle_pattern_rules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_alert_events_DedupeKey",
                table: "alert_events",
                column: "DedupeKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "candle_pattern_rules");

            migrationBuilder.DropIndex(
                name: "IX_alert_events_DedupeKey",
                table: "alert_events");

            migrationBuilder.DropColumn(
                name: "DedupeKey",
                table: "alert_events");
        }
    }
}
