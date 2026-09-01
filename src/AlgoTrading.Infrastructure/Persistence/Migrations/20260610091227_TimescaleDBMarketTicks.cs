using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TimescaleDBMarketTicks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_market_ticks",
                table: "market_ticks");

            migrationBuilder.AddPrimaryKey(
                name: "PK_market_ticks",
                table: "market_ticks",
                columns: new[] { "Id", "ReceivedUtc" });

            // 1. Convert to Hypertable (keeps data intact)
            migrationBuilder.Sql(@"SELECT create_hypertable('market_ticks', 'ReceivedUtc', chunk_time_interval => INTERVAL '1 day', migrate_data => true);");

            // 2. Enable TimescaleDB Compression
            migrationBuilder.Sql(@"
                ALTER TABLE market_ticks SET (
                    timescaledb.compress,
                    timescaledb.compress_segmentby = '""Symbol""',
                    timescaledb.compress_orderby = '""ReceivedUtc"" DESC'
                );
            ");

            // 3. Add continuous compression policy (compress data older than 7 days)
            migrationBuilder.Sql(@"SELECT add_compression_policy('market_ticks', compress_after => INTERVAL '7 days');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_market_ticks",
                table: "market_ticks");

            migrationBuilder.AddPrimaryKey(
                name: "PK_market_ticks",
                table: "market_ticks",
                column: "Id");
        }
    }
}
