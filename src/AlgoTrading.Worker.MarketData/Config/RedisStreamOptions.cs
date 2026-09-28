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

    /// <summary>
    /// How long an entry must sit unacknowledged before it is claimed and tried
    /// again. A batch whose database write failed stays in the group's pending
    /// list; before 28 Sep nothing ever read that list, so every such batch was
    /// lost for good while the worker carried on as if nothing had happened.
    /// </summary>
    public long ClaimMinIdleMs { get; set; } = 60_000;

    /// <summary>How many pending entries one pass claims back.</summary>
    public int ClaimBatchSize { get; set; } = 100;

    /// <summary>
    /// Deliveries before an entry is given up on and dead-lettered. With the
    /// defaults a tick is retried for about five minutes; the last try is made
    /// on its own, so one bad row cannot take a whole batch with it.
    /// </summary>
    public int MaxDeliveries { get; set; } = 5;

    /// <summary>
    /// Where an entry that could not be stored goes, with the reason — never
    /// simply dropped. <c>redis-cli XRANGE market:ticks:dead - +</c> lists them.
    /// </summary>
    public string DeadLetterStreamName { get; set; } = "market:ticks:dead";

    /// <summary>Cap on the dead-letter stream, approximate, like the tick stream's own.</summary>
    public int DeadLetterMaxLength { get; set; } = 100_000;

    /// <summary>
    /// Whether this worker also writes <c>live_quotes_latest</c>. Off by default:
    /// every live feed posts each tick to the API, and the API is the one writer
    /// of the latest-quote table. With both writing, a read here and a newer
    /// write there could interleave and put an older price back on record. Turn
    /// it on only when nothing posts to the API — a replay published straight
    /// to Redis by market_data/historical/db_replayer.py.
    /// </summary>
    public bool ProjectLatestQuotes { get; set; }
}
