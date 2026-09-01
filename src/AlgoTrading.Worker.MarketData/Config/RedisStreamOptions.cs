// src/AlgoTrading.Worker.MarketData/Configuration/RedisStreamOptions.cs
namespace AlgoTrading.Worker.MarketData.Configuration;

public class RedisStreamOptions
{
    public string ConnectionString { get; set; } = "localhost:6379";
    public string StreamName { get; set; } = "market:ticks";
    public string ConsumerGroup { get; set; } = "market-data-workers";
    public string ConsumerName { get; set; } = "market-worker-1";
    public int ReadBatchSize { get; set; } = 200;
    public int PollDelayMs { get; set; } = 500;
}
