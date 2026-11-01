using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MutualFunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mf_nav",
                columns: table => new
                {
                    SchemeCode = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Nav = table.Column<decimal>(type: "numeric", nullable: false),
                    Source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mf_nav", x => new { x.SchemeCode, x.Date });
                });

            migrationBuilder.CreateTable(
                name: "mf_schemes",
                columns: table => new
                {
                    SchemeCode = table.Column<int>(type: "integer", nullable: false),
                    IsinGrowth = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    IsinReinvest = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    Amc = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    SchemeName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Plan = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Option = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    OptionLabel = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Category = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FirstNavDate = table.Column<DateOnly>(type: "date", nullable: true),
                    LastNavDate = table.Column<DateOnly>(type: "date", nullable: true),
                    LastNav = table.Column<decimal>(type: "numeric", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mf_schemes", x => x.SchemeCode);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mf_schemes_Amc",
                table: "mf_schemes",
                column: "Amc");

            migrationBuilder.CreateIndex(
                name: "IX_mf_schemes_IsinGrowth",
                table: "mf_schemes",
                column: "IsinGrowth");

            // A hypertable like option_history_bars: written once, then only read,
            // one scheme over a date range at a time. About 14,000 schemes a day
            // and history back to April 2006 makes tens of millions of rows.
            //
            // 90-day chunks hold roughly 0.6 million rows today (one AMFI history
            // window's worth). Segmented by scheme and ordered by date, a
            // compressed batch is one scheme's consecutive NAVs, about 60 of them
            // per chunk. A chunk is compressed once it is 30 days old; the daily
            // sync writes only new days and corrections to recent ones, so it
            // almost never touches a compressed chunk, and when it does
            // (a scheme first seen with an old NAV) INSERT ... ON CONFLICT works
            // there too (TimescaleDB 2.11 and later).
            //
            // No retention policy: the history is the point.
            migrationBuilder.Sql(@"SELECT create_hypertable('mf_nav', 'Date', chunk_time_interval => INTERVAL '90 days', migrate_data => true);");
            migrationBuilder.Sql(@"
                ALTER TABLE mf_nav SET (
                    timescaledb.compress,
                    timescaledb.compress_segmentby = '""SchemeCode""',
                    timescaledb.compress_orderby = '""Date"" DESC'
                );
            ");
            migrationBuilder.Sql(@"SELECT add_compression_policy('mf_nav', compress_after => INTERVAL '30 days');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping a hypertable removes its chunks and its compression policy with it.
            migrationBuilder.DropTable(
                name: "mf_nav");

            migrationBuilder.DropTable(
                name: "mf_schemes");
        }
    }
}
