using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgoTrading.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChainCapturedUtcIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Built by hand on the server on 12 Oct 2026 during a session, concurrently so the chain recorder never
            // waited; IF NOT EXISTS makes this a no-op there and builds it on any other database without locking writes.
            migrationBuilder.Sql(
                "CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_chain_captured_utc ON option_chain_snapshots (\"CapturedUtc\");",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX CONCURRENTLY IF EXISTS ix_chain_captured_utc;", suppressTransaction: true);
        }
    }
}
