using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ComplianceFollowUps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "compliance_follow_ups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CircularId = table.Column<long>(type: "bigint", nullable: false),
                    ReportId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AlertedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AlertText = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    AlertDelivered = table.Column<bool>(type: "boolean", nullable: false),
                    HandledUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HandledBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: ""),
                    HandledNote = table.Column<string>(type: "text", nullable: false, defaultValue: "")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_compliance_follow_ups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_compliance_follow_ups_regulatory_circulars_CircularId",
                        column: x => x.CircularId,
                        principalTable: "regulatory_circulars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_compliance_follow_ups_CircularId",
                table: "compliance_follow_ups",
                column: "CircularId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "compliance_follow_ups");
        }
    }
}
