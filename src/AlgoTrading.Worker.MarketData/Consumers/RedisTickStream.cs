// src/AlgoTrading.Worker.MarketData/Consumers/RedisTickStream.cs
using AlgoTrading.Worker.MarketData.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace AlgoTrading.Worker.MarketData.Consumers;

/// <summary><see cref="ITickStream"/> over a Redis consumer group.</summary>
public sealed class RedisTickStream : ITickStream
{
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisStreamOptions _options;
    private readonly ILogger<RedisTickStream> _logger;

    public RedisTickStream(
        IConnectionMultiplexer redis,
        IOptions<RedisStreamOptions> options,
        ILogger<RedisTickStream> logger)
    {
        _redis = redis;
        _options = options.Value;
        _logger = logger;
    }

    private IDatabase Db => _redis.GetDatabase();

    public async Task EnsureGroupAsync()
    {
        try
        {
            // "$", not "0": the group is created once and then remembers its
            // place across restarts, so the only ticks it never sees are those
            // published before it first existed — which the API had already
            // stored in live_ticks. "0" would push up to the stream's whole cap
            // (500,000 entries) through the archive on the first start.
            await Db.StreamCreateConsumerGroupAsync(
                _options.StreamName,
                _options.ConsumerGroup,
                "$",
                createStream: true);

            _logger.LogInformation(
                "Created Redis consumer group. Stream={Stream}, Group={Group}",
                _options.StreamName,
                _options.ConsumerGroup);
        }
        catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Redis consumer group already exists. Stream={Stream}, Group={Group}",
                _options.StreamName,
                _options.ConsumerGroup);
        }
    }

    public async Task<IReadOnlyList<TickStreamEntry>> ReadNewAsync(int count)
    {
        var entries = await Db.StreamReadGroupAsync(
            _options.StreamName,
            _options.ConsumerGroup,
            _options.ConsumerName,
            ">",
            count: count);

        return entries.Select(ToEntry).ToList();
    }

    public async Task<TickStreamClaim> ClaimIdleAsync(long minIdleMs, string startId, int count)
    {
        var result = await Db.StreamAutoClaimAsync(
            _options.StreamName,
            _options.ConsumerGroup,
            _options.ConsumerName,
            minIdleMs,
            startId,
            count);

        if (result.IsNull)
        {
            return new TickStreamClaim([], "0-0", []);
        }

        return new TickStreamClaim(
            result.ClaimedEntries.Select(ToEntry).ToList(),
            result.NextStartId.IsNullOrEmpty ? "0-0" : result.NextStartId.ToString(),
            (result.DeletedIds ?? []).Select(x => x.ToString()).ToList());
    }

    public async Task<IReadOnlyDictionary<string, long>> DeliveryCountsAsync(IReadOnlyList<string> ids)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        if (ids.Count == 0) return counts;

        var ordered = ids.OrderBy(StreamIdOrder.Key).ToList();

        // One range query covers the claim; the margin allows for this
        // consumer's own not-yet-idle entries interleaved in the same range.
        var pending = await Db.StreamPendingMessagesAsync(
            _options.StreamName,
            _options.ConsumerGroup,
            ids.Count + _options.ReadBatchSize + 16,
            _options.ConsumerName,
            ordered[0],
            ordered[^1]);

        var wanted = new HashSet<string>(ids, StringComparer.Ordinal);
        foreach (var info in pending)
        {
            var id = info.MessageId.ToString();
            if (wanted.Contains(id)) counts[id] = info.DeliveryCount;
        }

        // Anything the range left out is asked for on its own: a count that
        // is never learned is an entry that is never given up on.
        foreach (var id in ids.Where(x => !counts.ContainsKey(x)))
        {
            var one = await Db.StreamPendingMessagesAsync(
                _options.StreamName, _options.ConsumerGroup, 1, _options.ConsumerName, id, id);
            if (one.Length > 0) counts[id] = one[0].DeliveryCount;
        }

        return counts;
    }

    public async Task AckAsync(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return;

        await Db.StreamAcknowledgeAsync(
            _options.StreamName,
            _options.ConsumerGroup,
            ids.Select(x => (RedisValue)x).ToArray());
    }

    public async Task DeadLetterAsync(TickStreamEntry entry, string reason, long deliveries)
    {
        await Db.StreamAddAsync(
            _options.DeadLetterStreamName,
            [
                new NameValueEntry("payload", entry.Payload ?? string.Empty),
                new NameValueEntry("sourceStream", _options.StreamName),
                new NameValueEntry("sourceId", entry.Id),
                new NameValueEntry("reason", reason),
                new NameValueEntry("deliveries", deliveries),
                new NameValueEntry("deadLetteredUtc", DateTime.UtcNow.ToString("O")),
            ],
            maxLength: _options.DeadLetterMaxLength,
            useApproximateMaxLength: true);
    }

    private static TickStreamEntry ToEntry(StreamEntry entry)
    {
        // Redis 6.2 answers a claim of a trimmed entry with a nil entry; 7
        // lists it under DeletedIds instead.
        if (entry.IsNull || entry.Values is null)
        {
            return new TickStreamEntry(entry.Id.ToString(), null, Trimmed: true);
        }

        var payload = entry.Values.FirstOrDefault(x => x.Name == "payload").Value;
        return new TickStreamEntry(entry.Id.ToString(), payload.IsNullOrEmpty ? null : payload.ToString());
    }
}

/// <summary>Stream ids ("1727499123456-3") in the order Redis keeps them.</summary>
public static class StreamIdOrder
{
    public static (long Ms, long Seq) Key(string id)
    {
        var dash = id.IndexOf('-');
        if (dash < 0) return (long.TryParse(id, out var only) ? only : 0, 0);
        long.TryParse(id.AsSpan(0, dash), out var ms);
        long.TryParse(id.AsSpan(dash + 1), out var seq);
        return (ms, seq);
    }

    /// <summary>When Redis stamped the entry: the id's millisecond part.</summary>
    public static DateTime? Time(string id)
    {
        var (ms, _) = Key(id);
        return ms > 0 ? DateTime.UnixEpoch.AddMilliseconds(ms) : null;
    }
}
