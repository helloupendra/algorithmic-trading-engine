using System.Text.Json;
using AlgoTrading.Worker.MarketData.Configuration;
using AlgoTrading.Worker.MarketData.Consumers;
using AlgoTrading.Worker.MarketData.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The market-data worker's drain from Redis to the database.
///
/// Until 28 Sep it read only new entries: a batch whose write failed stayed in
/// the consumer group's pending list and nothing ever read it again, so those
/// ticks were lost without a trace beyond one "loop failed" line. These pin
/// the replacement: claimed back once idle, retried a bounded number of times,
/// then dead-lettered with the reason — never simply dropped.
/// </summary>
public class TickStreamDrainTests
{
    private static readonly RedisStreamOptions Options = new()
    {
        ReadBatchSize = 50,
        ClaimMinIdleMs = 60_000,
        ClaimBatchSize = 100,
        MaxDeliveries = 3,
        DeadLetterStreamName = "market:ticks:dead",
    };

    private static string Tick(string symbol, string? receivedUtc = "2026-09-28T04:00:00.123456+00:00") =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["symbol"] = symbol,
            ["lastTradedPrice"] = 25010.5,
            ["exchangeTimestampUtc"] = "2026-09-28T04:00:00Z",
            ["receivedUtc"] = receivedUtc,
        });

    private sealed class Recorder
    {
        public readonly List<(List<string> Symbols, bool Redelivered)> Calls = new();
        public Func<IReadOnlyList<MarketTickStreamMessage>, bool>? Fails;

        public Task Process(IReadOnlyList<MarketTickStreamMessage> messages, bool redelivered, CancellationToken _)
        {
            Calls.Add((messages.Select(x => x.Symbol).ToList(), redelivered));
            if (Fails?.Invoke(messages) == true) throw new InvalidOperationException("database refused the batch");
            return Task.CompletedTask;
        }

        public List<string> Stored(bool? redelivered = null) => Calls
            .Where(c => redelivered is null || c.Redelivered == redelivered)
            .SelectMany(c => c.Symbols)
            .ToList();
    }

    private static (TickStreamDrain Drain, FakeTickStream Stream, Recorder Db) Build(params string[] payloads)
    {
        var stream = new FakeTickStream();
        foreach (var payload in payloads) stream.Add(payload);
        var db = new Recorder();
        var drain = new TickStreamDrain(stream, db.Process, Options, NullLogger.Instance);
        return (drain, stream, db);
    }

    [Fact]
    public async Task A_batch_that_failed_is_claimed_back_once_idle_and_stored()
    {
        var (drain, stream, db) = Build(Tick("NSE:NIFTY50-INDEX"), Tick("NSE:NIFTYBANK-INDEX"));
        db.Fails = _ => db.Calls.Count == 1;

        await drain.RunOnceAsync(CancellationToken.None);
        Assert.True(drain.LastPassFailed);
        Assert.Equal(2, stream.PendingCount);

        stream.AdvanceMs(Options.ClaimMinIdleMs);
        await drain.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "NSE:NIFTY50-INDEX", "NSE:NIFTYBANK-INDEX" }, db.Stored(redelivered: true));
        Assert.Equal(0, stream.PendingCount);
        Assert.Equal(2, drain.Stored);
        Assert.Empty(stream.DeadLetters);
    }

    [Fact]
    public async Task A_pending_entry_is_not_retried_before_it_has_been_idle_long_enough()
    {
        var (drain, stream, db) = Build(Tick("NSE:NIFTY50-INDEX"));
        db.Fails = _ => true;

        await drain.RunOnceAsync(CancellationToken.None);
        stream.AdvanceMs(Options.ClaimMinIdleMs - 1);
        await drain.RunOnceAsync(CancellationToken.None);

        Assert.Single(db.Calls);
        Assert.Equal(1, stream.PendingCount);
    }

    [Fact]
    public async Task An_entry_that_never_stores_is_dead_lettered_after_the_last_delivery_not_dropped()
    {
        var (drain, stream, db) = Build(Tick("NSE:NIFTY50-INDEX"));
        db.Fails = _ => true;

        for (var pass = 0; pass < 10; pass++)
        {
            await drain.RunOnceAsync(CancellationToken.None);
            stream.AdvanceMs(Options.ClaimMinIdleMs);
        }

        // Tried exactly MaxDeliveries times, then kept — with its reason — and
        // acknowledged so it stops coming round.
        Assert.Equal(Options.MaxDeliveries, db.Calls.Count);
        var dead = Assert.Single(stream.DeadLetters);
        Assert.Equal("1-0", dead.Entry.Id);
        Assert.Contains("not stored after 3 deliveries", dead.Reason);
        Assert.Contains("NSE:NIFTY50-INDEX", dead.Entry.Payload);
        Assert.Equal(0, stream.PendingCount);
        Assert.Equal(1, drain.DeadLettered);
    }

    [Fact]
    public async Task The_last_try_is_made_alone_so_one_bad_row_does_not_take_its_batch_with_it()
    {
        var (drain, stream, db) = Build(Tick("NSE:NIFTY50-INDEX"), Tick("NSE:BAD"), Tick("NSE:NIFTYBANK-INDEX"));
        db.Fails = messages => messages.Any(x => x.Symbol == "NSE:BAD");

        for (var pass = 0; pass < 10; pass++)
        {
            await drain.RunOnceAsync(CancellationToken.None);
            stream.AdvanceMs(Options.ClaimMinIdleMs);
        }

        Assert.Equal(new[] { "NSE:NIFTY50-INDEX", "NSE:NIFTYBANK-INDEX" },
            db.Calls.Where(c => c.Symbols.Count == 1 && c.Symbols[0] != "NSE:BAD").SelectMany(c => c.Symbols));
        var dead = Assert.Single(stream.DeadLetters);
        Assert.Contains("NSE:BAD", dead.Entry.Payload);
        Assert.Equal(0, stream.PendingCount);
        Assert.Equal(2, drain.Stored);
    }

    [Fact]
    public async Task A_payload_that_is_not_a_tick_is_dead_lettered_at_once_instead_of_acknowledged_and_forgotten()
    {
        var (drain, stream, db) = Build("{not json", Tick("NSE:NIFTY50-INDEX"));

        await drain.RunOnceAsync(CancellationToken.None);

        var dead = Assert.Single(stream.DeadLetters);
        Assert.Equal("{not json", dead.Entry.Payload);
        Assert.Contains("not a tick", dead.Reason);
        Assert.Equal(new[] { "NSE:NIFTY50-INDEX" }, db.Stored());
        Assert.Equal(0, stream.PendingCount);
    }

    [Fact]
    public async Task An_entry_with_no_payload_is_dead_lettered_too()
    {
        var (drain, stream, _) = Build();
        stream.Add(payload: null);

        await drain.RunOnceAsync(CancellationToken.None);

        Assert.Equal("empty payload", Assert.Single(stream.DeadLetters).Reason);
        Assert.Equal(0, stream.PendingCount);
    }

    [Fact]
    public async Task A_pending_tick_the_stream_trimmed_is_counted_as_lost_not_passed_over()
    {
        var (drain, stream, db) = Build(Tick("NSE:NIFTY50-INDEX"));
        db.Fails = _ => true;

        await drain.RunOnceAsync(CancellationToken.None);
        stream.Trim("1-0");
        stream.AdvanceMs(Options.ClaimMinIdleMs);
        await drain.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, drain.Lost);
        Assert.Equal(0, stream.PendingCount);
    }

    [Fact]
    public async Task A_tick_without_a_publish_stamp_takes_its_entrys_clock_so_a_redelivery_is_the_same_tick()
    {
        var (drain, stream, _) = Build();
        stream.Add(Tick("NSE:NIFTY50-INDEX", receivedUtc: null), id: "1727499600123-0");
        MarketTickStreamMessage? seen = null;
        var capture = new TickStreamDrain(stream, (messages, _, _) =>
        {
            seen = messages[0];
            return Task.CompletedTask;
        }, Options, NullLogger.Instance);

        await capture.RunOnceAsync(CancellationToken.None);

        Assert.Equal(DateTime.UnixEpoch.AddMilliseconds(1727499600123), seen!.ReceivedUtc);
    }

    [Fact]
    public async Task Nothing_new_and_nothing_due_is_an_idle_pass()
    {
        var (drain, _, db) = Build();
        Assert.Equal(0, await drain.RunOnceAsync(CancellationToken.None));
        Assert.False(drain.LastPassFailed);
        Assert.Empty(db.Calls);
    }

    [Theory]
    [InlineData("1-0", "2-0", -1)]
    [InlineData("10-0", "9-5", 1)]
    [InlineData("5-2", "5-10", -1)]
    public void Stream_ids_order_numerically_not_as_text(string a, string b, int sign)
    {
        Assert.Equal(sign, Math.Sign(StreamIdOrder.Key(a).CompareTo(StreamIdOrder.Key(b))));
    }
}

/// <summary>
/// A consumer group in memory: ids "1-0", "2-0", … unless given, a pending list
/// with delivery counts and a clock the test moves.
/// </summary>
internal sealed class FakeTickStream : ITickStream
{
    private readonly List<TickStreamEntry> _entries = new();
    private readonly HashSet<string> _trimmed = new();
    private readonly Dictionary<string, (long Deliveries, long DeliveredAtMs)> _pending = new();
    private int _nextNew;
    private long _nowMs;

    public readonly List<(TickStreamEntry Entry, string Reason, long Deliveries)> DeadLetters = new();

    public int PendingCount => _pending.Count;

    public void Add(string? payload, string? id = null) =>
        _entries.Add(new TickStreamEntry(id ?? $"{_entries.Count + 1}-0", payload));

    public void AdvanceMs(long ms) => _nowMs += ms;

    /// <summary>The length cap removes the entry from the stream; the pending list still names it.</summary>
    public void Trim(string id) => _trimmed.Add(id);

    public Task EnsureGroupAsync() => Task.CompletedTask;

    public Task<IReadOnlyList<TickStreamEntry>> ReadNewAsync(int count)
    {
        var batch = _entries.Skip(_nextNew).Take(count).ToList();
        _nextNew += batch.Count;
        foreach (var entry in batch) _pending[entry.Id] = (1, _nowMs);
        return Task.FromResult<IReadOnlyList<TickStreamEntry>>(batch);
    }

    public Task<TickStreamClaim> ClaimIdleAsync(long minIdleMs, string startId, int count)
    {
        var due = _pending
            .Where(p => StreamIdOrder.Key(p.Key).CompareTo(StreamIdOrder.Key(startId)) >= 0)
            .Where(p => _nowMs - p.Value.DeliveredAtMs >= minIdleMs)
            .OrderBy(p => StreamIdOrder.Key(p.Key))
            .Take(count)
            .Select(p => p.Key)
            .ToList();

        var claimed = new List<TickStreamEntry>();
        var deleted = new List<string>();
        foreach (var id in due)
        {
            if (_trimmed.Contains(id))
            {
                // Redis 7: reported as deleted and dropped from the pending list.
                _pending.Remove(id);
                deleted.Add(id);
                continue;
            }

            _pending[id] = (_pending[id].Deliveries + 1, _nowMs);
            claimed.Add(_entries.Single(e => e.Id == id));
        }

        return Task.FromResult(new TickStreamClaim(claimed, "0-0", deleted));
    }

    public Task<IReadOnlyDictionary<string, long>> DeliveryCountsAsync(IReadOnlyList<string> ids) =>
        Task.FromResult<IReadOnlyDictionary<string, long>>(ids
            .Where(_pending.ContainsKey)
            .ToDictionary(id => id, id => _pending[id].Deliveries));

    public Task AckAsync(IReadOnlyList<string> ids)
    {
        foreach (var id in ids) _pending.Remove(id);
        return Task.CompletedTask;
    }

    public Task DeadLetterAsync(TickStreamEntry entry, string reason, long deliveries)
    {
        DeadLetters.Add((entry, reason, deliveries));
        return Task.CompletedTask;
    }
}
