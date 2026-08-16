// src/AlgoTrading.Worker.MarketData/Program.cs
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Worker.MarketData.Configuration;
using AlgoTrading.Worker.MarketData.Consumers;
using AlgoTrading.Worker.MarketData.Processing;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

// Local overrides for secrets (DB password, Redis password). Generated from the
// repo-root .env by scripts/setup.sh|ps1 and git-ignored, so real credentials
// never reach a tracked file. Added last so it wins over appsettings.json.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Configuration
builder.Services.Configure<RedisStreamOptions>(
    builder.Configuration.GetSection("Redis"));

// Redis
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RedisStreamOptions>>().Value;
    return ConnectionMultiplexer.Connect(options.ConnectionString);
});

// PostgreSQL / EF Core
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
                      ?? throw new InvalidOperationException("DefaultConnection was not found.");

builder.Services.AddDbContext<TradingDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

// Processing
builder.Services.AddScoped<ITickBatchProcessor, TickBatchProcessor>();

// Worker
builder.Services.AddHostedService<RedisTickConsumerService>();

var host = builder.Build();
await host.RunAsync();