using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace AlgoTrading.IntegrationTests;

public sealed class MigrationHistoryTests
{
    [TimescaleFact]
    public async Task Fresh_database_runs_full_history_and_latest_rollback_preserves_existing_accounts()
    {
        var admin = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("OPENFNO_TEST_TIMESCALE")) { Database = "postgres", Pooling = false };
        var name = $"migration_test_{Guid.NewGuid():N}";
        async Task Execute(string sql) {
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        await Execute($"CREATE DATABASE \"{name}\"");
        try {
            var connection = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = name };
            await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseNpgsql(connection.ConnectionString).Options);
            await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS timescaledb");
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            db.AppUsers.Add(new() { Id = 7, UserName = "rollback-sentinel", Email = "sentinel@example.invalid" });
            db.MarketSpecialSessions.Add(new() { Exchange = "NSE", Date = new(2026, 11, 8), Name = "Muhurat", OpenIst = new(14, 20), CloseIst = new(15, 40), CashCloseIst = new(15, 30) });
            db.ManualOrderReceipts.Add(new ManualOrderReceipt { UserId = 7, ClientOrderId = Guid.NewGuid(), RequestHash = "migration-probe", FillJson = "{}", CreatedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var migrations = db.Database.GetMigrations().ToArray();
            Assert.EndsWith("SpecialSessionCashClose", migrations[^1]);
            await db.GetService<IMigrator>().MigrateAsync(migrations[^2]);
            db.ChangeTracker.Clear();
            Assert.Equal("rollback-sentinel", (await db.AppUsers.SingleAsync()).UserName);
            Assert.Single(await db.ManualOrderReceipts.ToListAsync());
            Assert.Equal(1, await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM market_special_sessions WHERE "Exchange" = 'NSE' AND "OpenIst" = '14:20' AND "CloseIst" = '15:40'""").SingleAsync());
            await db.Database.MigrateAsync();
            var session = await db.MarketSpecialSessions.SingleAsync();
            Assert.Null(session.CashCloseIst);
            session.CashCloseIst = new(15, 30);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            Assert.Equal(new TimeOnly(15, 30), (await db.MarketSpecialSessions.SingleAsync()).CashCloseIst);
            Assert.Single(await db.ManualOrderReceipts.ToListAsync());
        }
        finally { await Execute($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)"); }
    }
}
public sealed class TimescaleFactAttribute : FactAttribute
{
    public TimescaleFactAttribute() {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENFNO_TEST_TIMESCALE")))
            Skip = "Set OPENFNO_TEST_TIMESCALE to a disposable TimescaleDB test server.";
    }
}
