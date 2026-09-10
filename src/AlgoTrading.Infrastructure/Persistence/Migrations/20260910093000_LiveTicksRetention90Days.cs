using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Keep raw ticks for 90 days instead of 7.
    /// </summary>
    /// <remarks>
    /// The 7-day policy came in with the hypertable on 2026-09-09, when the
    /// concern was insert latency. With a chunk per day and compression after
    /// two, the age of the data no longer costs the writers anything, and the
    /// owner wants every tick kept for analysis. Compressed chunks run ~10x
    /// smaller than the ~1 GB an uncompressed trading day takes, so 90 days
    /// is on the order of 10 GB on the live box. Beyond 90 days the plan is
    /// to export the chunks before they are dropped, not to keep growing.
    ///
    /// No model change: this migration only swaps the TimescaleDB policy, so
    /// it carries no Designer file and leaves the snapshot alone.
    /// </remarks>
    [DbContext(typeof(TradingDbContext))]
    [Migration("20260910093000_LiveTicksRetention90Days")]
    public partial class LiveTicksRetention90Days : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"SELECT remove_retention_policy('live_ticks', if_exists => true);");
            migrationBuilder.Sql(@"SELECT add_retention_policy('live_ticks', drop_after => INTERVAL '90 days');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"SELECT remove_retention_policy('live_ticks', if_exists => true);");
            migrationBuilder.Sql(@"SELECT add_retention_policy('live_ticks', drop_after => INTERVAL '7 days');");
        }
    }
}
