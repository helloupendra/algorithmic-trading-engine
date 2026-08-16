using Microsoft.Extensions.Logging;
using Npgsql;
using System;
using System.Threading.Tasks;

namespace ExecutionEngine.Services
{
    public class TickRepository
    {
        private readonly ILogger<TickRepository> _logger;
        private readonly string _connectionString;

        public TickRepository(ILogger<TickRepository> logger)
        {
            _logger = logger;
            // Fetch the DB URL injected by Docker, or default to localhost for local debugging
            _connectionString = Environment.GetEnvironmentVariable("DATABASE_URL") 
                                ?? "Host=localhost;Port=5432;Database=algotrading;Username=algo_user;Password=algo_password";
        }

        public async Task InsertTickAsync(string symbol, double lastTradedPrice, long volume)
        {
            try
            {
                await using var conn = new NpgsqlConnection(_connectionString);
                await conn.OpenAsync();

                // Standard SQL insert. TimescaleDB automatically routes this into the correct time chunk!
                var sql = "INSERT INTO market_ticks (time, symbol, last_traded_price, volume) VALUES (@time, @symbol, @price, @volume)";
                
                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("time", DateTime.UtcNow);
                cmd.Parameters.AddWithValue("symbol", symbol);
                cmd.Parameters.AddWithValue("price", lastTradedPrice);
                cmd.Parameters.AddWithValue("volume", volume);

                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to insert tick data for {Symbol}", symbol);
            }
        }
    }
}