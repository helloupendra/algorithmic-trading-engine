using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Incidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "incidents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Fingerprint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Agent = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Rule = table.Column<string>(type: "text", nullable: false),
                    Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    Location = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    EvidenceJson = table.Column<string>(type: "text", nullable: true),
                    Suggestion = table.Column<string>(type: "text", nullable: true),
                    Occurrences = table.Column<int>(type: "integer", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AcknowledgedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    NotifiedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incidents", x => x.Id);
                    table.CheckConstraint("CK_incidents_Severity", "\"Severity\" IN ('low', 'medium', 'high', 'critical')");
                    table.CheckConstraint("CK_incidents_Status", "\"Status\" IN ('open', 'acknowledged', 'resolved')");
                });

            migrationBuilder.CreateTable(
                name: "sentinel_heartbeat",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    LastCheckUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sentinel_heartbeat", x => x.Id);
                    table.CheckConstraint("CK_sentinel_heartbeat_single_row", "\"Id\" = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_incidents_Fingerprint",
                table: "incidents",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_Status_LastSeenUtc",
                table: "incidents",
                columns: new[] { "Status", "LastSeenUtc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "UX_incidents_Fingerprint_live",
                table: "incidents",
                column: "Fingerprint",
                unique: true,
                filter: "\"Status\" IN ('open', 'acknowledged')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "incidents");

            migrationBuilder.DropTable(
                name: "sentinel_heartbeat");
        }
    }
}
