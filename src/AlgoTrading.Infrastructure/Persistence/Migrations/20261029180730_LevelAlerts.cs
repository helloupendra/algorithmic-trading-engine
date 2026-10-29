using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LevelAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "level_alert_fires",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RuleId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    FiredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Target = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Condition = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Level = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    Value = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    ValueAsOfUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PreviousValue = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    PreviousAsOfUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    DeliveredToTelegram = table.Column<bool>(type: "boolean", nullable: false),
                    TelegramNote = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_level_alert_fires", x => x.Id);
                    table.ForeignKey(
                        name: "FK_level_alert_fires_app_user_UserId",
                        column: x => x.UserId,
                        principalTable: "app_user",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "level_alert_rules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Target = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Condition = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Level = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    Repeat = table.Column<bool>(type: "boolean", nullable: false),
                    RearmBy = table.Column<decimal>(type: "numeric(18,6)", nullable: true),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Notify = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Armed = table.Column<bool>(type: "boolean", nullable: false),
                    LastFiredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FireCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_level_alert_rules", x => x.Id);
                    table.CheckConstraint("CK_level_alert_rules_Condition", "\"Condition\" IN ('above', 'below', 'touch')");
                    table.CheckConstraint("CK_level_alert_rules_Kind", "\"Kind\" IN ('price', 'pcr')");
                    table.CheckConstraint("CK_level_alert_rules_Status", "\"Status\" IN ('active', 'paused', 'fired')");
                    table.ForeignKey(
                        name: "FK_level_alert_rules_app_user_UserId",
                        column: x => x.UserId,
                        principalTable: "app_user",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_level_alert_fires_FiredUtc",
                table: "level_alert_fires",
                column: "FiredUtc");

            migrationBuilder.CreateIndex(
                name: "IX_level_alert_fires_RuleId",
                table: "level_alert_fires",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_level_alert_fires_UserId_FiredUtc",
                table: "level_alert_fires",
                columns: new[] { "UserId", "FiredUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_level_alert_rules_Status",
                table: "level_alert_rules",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_level_alert_rules_UserId",
                table: "level_alert_rules",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "level_alert_fires");

            migrationBuilder.DropTable(
                name: "level_alert_rules");
        }
    }
}
