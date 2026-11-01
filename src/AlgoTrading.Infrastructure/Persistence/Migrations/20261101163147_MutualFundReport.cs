using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MutualFundReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mf_benchmark_tri",
                columns: table => new
                {
                    IndexName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Value = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mf_benchmark_tri", x => new { x.IndexName, x.Date });
                });

            migrationBuilder.CreateTable(
                name: "mf_scheme_stats",
                columns: table => new
                {
                    SchemeCode = table.Column<int>(type: "integer", nullable: false),
                    AsOfDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PeerKey = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    Plan = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Return1y = table.Column<double>(type: "double precision", nullable: true),
                    Return3y = table.Column<double>(type: "double precision", nullable: true),
                    Return5y = table.Column<double>(type: "double precision", nullable: true),
                    Rank1y = table.Column<int>(type: "integer", nullable: true),
                    Rank3y = table.Column<int>(type: "integer", nullable: true),
                    Rank5y = table.Column<int>(type: "integer", nullable: true),
                    Peers1y = table.Column<int>(type: "integer", nullable: true),
                    Peers3y = table.Column<int>(type: "integer", nullable: true),
                    Peers5y = table.Column<int>(type: "integer", nullable: true),
                    ComputedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mf_scheme_stats", x => x.SchemeCode);
                });

            migrationBuilder.CreateTable(
                name: "mf_ter",
                columns: table => new
                {
                    NsdlSchemeCode = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    SchemeName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    NameKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SchemeType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Category = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RegularTer = table.Column<decimal>(type: "numeric", nullable: true),
                    DirectTer = table.Column<decimal>(type: "numeric", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mf_ter", x => new { x.NsdlSchemeCode, x.Date });
                });

            migrationBuilder.CreateIndex(
                name: "IX_mf_scheme_stats_PeerKey_Plan",
                table: "mf_scheme_stats",
                columns: new[] { "PeerKey", "Plan" });

            migrationBuilder.CreateIndex(
                name: "IX_mf_ter_NameKey_Date",
                table: "mf_ter",
                columns: new[] { "NameKey", "Date" });

            // Two small time series, written once and then read, like mf_nav. mf_benchmark_tri holds a few indices'
            // daily closes from 2006 (about 5,000 rows each), so its chunks are a year long; segmented by index and
            // ordered by date, a compressed batch is one index's year. Compressed once a chunk is 30 days old: the
            // nightly sync rewrites only the last ten days.
            migrationBuilder.Sql(@"SELECT create_hypertable('mf_benchmark_tri', 'Date', chunk_time_interval => INTERVAL '365 days', migrate_data => true);");
            migrationBuilder.Sql(@"
                ALTER TABLE mf_benchmark_tri SET (
                    timescaledb.compress,
                    timescaledb.compress_segmentby = '""IndexName""',
                    timescaledb.compress_orderby = '""Date"" DESC'
                );
            ");
            migrationBuilder.Sql(@"SELECT add_compression_policy('mf_benchmark_tri', compress_after => INTERVAL '30 days');");

            // mf_ter: one row per scheme per day, about 62,000 a month. The sync rereads the current month every
            // night and the month before during the first days of a new one, so a chunk is compressed only once it is
            // 90 days old, never while it is still being written.
            migrationBuilder.Sql(@"SELECT create_hypertable('mf_ter', 'Date', chunk_time_interval => INTERVAL '365 days', migrate_data => true);");
            migrationBuilder.Sql(@"
                ALTER TABLE mf_ter SET (
                    timescaledb.compress,
                    timescaledb.compress_segmentby = '""NsdlSchemeCode""',
                    timescaledb.compress_orderby = '""Date"" DESC'
                );
            ");
            migrationBuilder.Sql(@"SELECT add_compression_policy('mf_ter', compress_after => INTERVAL '90 days');");

            // mf_scheme_stats is not a time series: one row per scheme, replaced each night.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping a hypertable removes its chunks and its compression policy with it.
            migrationBuilder.DropTable(
                name: "mf_benchmark_tri");

            migrationBuilder.DropTable(
                name: "mf_scheme_stats");

            migrationBuilder.DropTable(
                name: "mf_ter");
        }
    }
}
