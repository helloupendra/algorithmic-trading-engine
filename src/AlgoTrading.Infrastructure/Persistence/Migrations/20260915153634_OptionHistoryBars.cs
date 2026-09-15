using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OptionHistoryBars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "option_history_bars",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BarStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Underlying = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExpiryFlag = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ExpiryCode = table.Column<int>(type: "integer", nullable: false),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    StrikeOffset = table.Column<int>(type: "integer", nullable: false),
                    Strike = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    OptionType = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Resolution = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Open = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    High = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Low = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Close = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Volume = table.Column<long>(type: "bigint", nullable: true),
                    OpenInterest = table.Column<long>(type: "bigint", nullable: true),
                    ImpliedVolatility = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    SpotPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    SourceKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_option_history_bars", x => new { x.Id, x.BarStartUtc });
                });

            migrationBuilder.CreateIndex(
                name: "ux_option_history_series_time",
                table: "option_history_bars",
                columns: new[] { "Underlying", "ExpiryFlag", "ExpiryCode", "StrikeOffset", "OptionType", "Resolution", "BarStartUtc" },
                unique: true);

            // A hypertable like live_ticks and market_ticks, for a different
            // reason: this table is written in bulk and then only read. Two years
            // of NIFTY, BANKNIFTY and SENSEX at ATM±3 in 1-minute bars is about
            // eight million rows, and every read is one series over a date range.
            //
            // Week-long chunks keep a chunk to roughly the size of a 1-minute
            // import window. Everything imported is history, so the policy
            // compresses a chunk as soon as it is a week old; segmented by
            // underlying and ordered series-then-time, each compressed batch is
            // one series' consecutive bars. Re-imports stay idempotent on
            // compressed chunks: INSERT ... ON CONFLICT DO NOTHING checks the
            // unique index there too (TimescaleDB 2.11 and later).
            //
            // No retention policy: this is the research record, kept whole.
            migrationBuilder.Sql(@"SELECT create_hypertable('option_history_bars', 'BarStartUtc', chunk_time_interval => INTERVAL '7 days', migrate_data => true);");
            migrationBuilder.Sql(@"
                ALTER TABLE option_history_bars SET (
                    timescaledb.compress,
                    timescaledb.compress_segmentby = '""Underlying""',
                    timescaledb.compress_orderby = '""ExpiryFlag"", ""ExpiryCode"", ""Resolution"", ""StrikeOffset"", ""OptionType"", ""BarStartUtc"" DESC'
                );
            ");
            migrationBuilder.Sql(@"SELECT add_compression_policy('option_history_bars', compress_after => INTERVAL '7 days');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping a hypertable removes its chunks and its compression policy with it.
            migrationBuilder.DropTable(
                name: "option_history_bars");
        }
    }
}
