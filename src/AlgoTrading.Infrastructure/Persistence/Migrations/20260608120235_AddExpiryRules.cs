using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExpiryRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "expiry_rules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Exchange = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Underlying = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    HasWeekly = table.Column<bool>(type: "boolean", nullable: false),
                    HasMonthly = table.Column<bool>(type: "boolean", nullable: false),
                    HasQuarterly = table.Column<bool>(type: "boolean", nullable: false),
                    HasSemiAnnual = table.Column<bool>(type: "boolean", nullable: false),
                    WeeklyExpiryDay = table.Column<int>(type: "integer", nullable: true),
                    MonthlyExpiryDay = table.Column<int>(type: "integer", nullable: true),
                    QuarterlyExpiryDay = table.Column<int>(type: "integer", nullable: true),
                    SemiAnnualExpiryDay = table.Column<int>(type: "integer", nullable: true),
                    HolidayShiftRule = table.Column<int>(type: "integer", nullable: false),
                    PreferredExpiryType = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expiry_rules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_expiry_rules_Exchange_Underlying",
                table: "expiry_rules",
                columns: new[] { "Exchange", "Underlying" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "expiry_rules");
        }
    }
}
