using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LiveTicksHypertable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_live_ticks",
                table: "live_ticks");

            migrationBuilder.AddPrimaryKey(
                name: "PK_live_ticks",
                table: "live_ticks",
                columns: new[] { "Id", "ReceivedUtc" });

            // Why. live_ticks was a plain table at ~7.6M rows / 5.6 GB (seven days
            // of ticks, aged out row by row). Every insert paid random I/O on two
            // large B-trees; on the live box a 100-row insert took 250-800 ms, the
            // ingestor's batches timed out at 30 s, 28,500 ticks were shed on
            // 2026-09-09 and the quotes the risk guard read were 37 minutes old.
            // The same shape market_ticks already has: one chunk per day, so the
            // indexes being written are a day's worth and stay in cache; old
            // chunks compress ~10x after two days and are DROPPED whole after
            // seven (no DELETE, no dead rows, no bloat). The retention window
            // was widened to 90 days on 2026-09-10 (LiveTicksRetention90Days).
            migrationBuilder.Sql(@"SELECT create_hypertable('live_ticks', 'ReceivedUtc', chunk_time_interval => INTERVAL '1 day', migrate_data => true);");
            migrationBuilder.Sql(@"
                ALTER TABLE live_ticks SET (
                    timescaledb.compress,
                    timescaledb.compress_segmentby = '""Symbol""',
                    timescaledb.compress_orderby = '""ReceivedUtc"" DESC'
                );
            ");
            migrationBuilder.Sql(@"SELECT add_compression_policy('live_ticks', compress_after => INTERVAL '2 days');");
            migrationBuilder.Sql(@"SELECT add_retention_policy('live_ticks', drop_after => INTERVAL '7 days');");

            // The two small hot tables. live_quotes_latest has ~120 rows that are
            // each rewritten a few times a second; with default autovacuum it had
            // grown to 5.5 MB for 120 rows and a lookup by symbol cost 140 ms on
            // every batch. A low fillfactor keeps the updates HOT (in-page) and
            // the per-table thresholds vacuum it after a few hundred updates
            // instead of after 20% of a table that never grows.
            migrationBuilder.Sql(@"ALTER TABLE live_quotes_latest SET (fillfactor = 50, autovacuum_vacuum_scale_factor = 0, autovacuum_vacuum_threshold = 300, autovacuum_analyze_scale_factor = 0, autovacuum_analyze_threshold = 300);");
            migrationBuilder.Sql(@"ALTER TABLE live_bars SET (fillfactor = 70, autovacuum_vacuum_scale_factor = 0.01, autovacuum_vacuum_threshold = 1000);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A hypertable cannot be turned back into a plain table in place;
            // the policies and the table settings are undone, the composite key
            // stays valid for either shape.
            migrationBuilder.Sql(@"SELECT remove_retention_policy('live_ticks', if_exists => true);");
            migrationBuilder.Sql(@"SELECT remove_compression_policy('live_ticks', if_exists => true);");
            migrationBuilder.Sql(@"ALTER TABLE live_quotes_latest RESET (fillfactor, autovacuum_vacuum_scale_factor, autovacuum_vacuum_threshold, autovacuum_analyze_scale_factor, autovacuum_analyze_threshold);");
            migrationBuilder.Sql(@"ALTER TABLE live_bars RESET (fillfactor, autovacuum_vacuum_scale_factor, autovacuum_vacuum_threshold);");
            migrationBuilder.DropPrimaryKey(
                name: "PK_live_ticks",
                table: "live_ticks");

            migrationBuilder.AddPrimaryKey(
                name: "PK_live_ticks",
                table: "live_ticks",
                column: "Id");
        }
    }
}
