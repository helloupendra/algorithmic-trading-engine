using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Forecasts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "forecast_models",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Target = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    BacktestJson = table.Column<string>(type: "text", nullable: true),
                    RegisteredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_forecast_models", x => new { x.Key, x.Version });
                });

            migrationBuilder.CreateTable(
                name: "forecasts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ModelKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ModelVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Target = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Underlying = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SessionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IssuedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PredictionJson = table.Column<string>(type: "text", nullable: false),
                    BaselineJson = table.Column<string>(type: "text", nullable: false),
                    InputsJson = table.Column<string>(type: "text", nullable: false),
                    OutcomeJson = table.Column<string>(type: "text", nullable: true),
                    ScoresJson = table.Column<string>(type: "text", nullable: true),
                    Loss = table.Column<double>(type: "double precision", nullable: true),
                    BaselineLoss = table.Column<double>(type: "double precision", nullable: true),
                    ScoredUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_forecasts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_forecasts_forecast_models_ModelKey_ModelVersion",
                        columns: x => new { x.ModelKey, x.ModelVersion },
                        principalTable: "forecast_models",
                        principalColumns: new[] { "Key", "Version" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_forecasts_SessionDate",
                table: "forecasts",
                column: "SessionDate");

            migrationBuilder.CreateIndex(
                name: "UX_forecasts_model_target_underlying_session",
                table: "forecasts",
                columns: new[] { "ModelKey", "ModelVersion", "Target", "Underlying", "SessionDate" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "forecasts");

            migrationBuilder.DropTable(
                name: "forecast_models");
        }
    }
}
