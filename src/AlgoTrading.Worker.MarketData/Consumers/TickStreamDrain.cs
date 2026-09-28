// src/AlgoTrading.Worker.MarketData/Consumers/TickStreamDrain.cs
using System.Text.Json;
using AlgoTrading.Worker.MarketData.Configuration;
using AlgoTrading.Worker.MarketData.Models;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Worker.MarketData.Consumers;

/// <summary>
/// Moves ticks from the Redis stream into the database, at least once, and
/// never drops one without saying so.
/// </summary>
/// <remarks>
/// Until 28 Sep the consumer read only new entries ("&gt;"). A batch whose
/// write failed was left unacknowledged in the group's pending list, and
/// nothing ever looked there again: the ticks were gone, the log said "loop
/// failed" once, and the next batch carried on as if nothing had happened.
/// <para>
/// Each pass now first claims back what has sat pending for
/// <see cref="RedisStreamOptions.ClaimMinIdleMs"/> (XAUTOCLAIM) and tries it
/// again. An entry is tried <see cref="RedisStreamOptions.MaxDeliveries"/>
/// times: in its batch, then on the last try on its own, so one row the
/// database refuses cannot take the rest of its batch down with it. After that
/// it is copied to the dead-letter stream with the reason and acknowledged —
/// kept, logged and counted, never silently dropped. An entry that is not
/// valid JSON is dead-lettered at once: no retry will change it.
/// </para>
/// </remarks>
public sealed class TickStreamDrain
{
    public delegate Task ProcessBatch(
        IReadOnlyList<MarketTickStreamMessage> messages, bool redelivered, CancellationToken cancellationToken);

    private readonly ITickStream _stream;
    private readonly ProcessBatch _process;
    private readonly RedisStreamOptions _options;
    private readonly ILogger _logger;

    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Where the next claim continues in the pending list.</summary>
    private string _claimCursor = "0-0";

    public TickStreamDrain(ITickStream stream, ProcessBatch process, RedisStreamOptions options, ILogger logger)
    {
        _stream = stream;
        _process = process;
        _options = options;
        _logger = logger;
    }

    /// <summary>Ticks stored and acknowledged since the worker started.</summary>
    public long Stored { get; private set; }

    /// <summary>Ticks copied to the dead-letter stream since the worker started.</summary>
    public long DeadLettered { get; private set; }

    /// <summary>Pending ticks the stream trimmed before they could be stored: gone for good.</summary>
    public long Lost { get; private set; }

    /// <summary>
    /// True when the last pass could not store something. The caller backs off
    /// instead of reading batch after batch into a database that refuses them.
    /// </summary>
    public bool LastPassFailed { get; private set; }

    /// <summary>One pass: retry what is due, then read what is new. Returns the entries handled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        LastPassFailed = false;
        var handled = await RetryPendingAsync(cancellationToken);
        handled += await ReadNewAsync(cancellationToken);
        return handled;
    }

    private async Task<int> ReadNewAsync(CancellationToken cancellationToken)
    {
        var entries = await _stream.ReadNewAsync(_options.ReadBatchSize);
        if (entries.Count == 0) return 0;

        var parsed = await ParseOrDeadLetterAsync(entries, deliveries: 1);
        if (parsed.Count == 0) return entries.Count;

        if (await TryProcessAsync(parsed, redelivered: false, cancellationToken))
        {
            await AckStoredAsync(parsed);
        }

        return entries.Count;
    }

    private async Task<int> RetryPendingAsync(CancellationToken cancellationToken)
    {
        var claim = await _stream.ClaimIdleAsync(_options.ClaimMinIdleMs, _claimCursor, _options.ClaimBatchSize);
        _claimCursor = string.IsNullOrEmpty(claim.NextStartId) ? "0-0" : claim.NextStartId;

        var trimmed = claim.Entries.Where(x => x.Trimmed).Select(x => x.Id).ToList();
        if (trimmed.Count > 0)
        {
            // Redis 6.2 keeps a trimmed entry in the pending list; 7 drops it itself.
            await _stream.AckAsync(trimmed);
        }

        var lost = claim.DeletedIds.Count + trimmed.Count;
        if (lost > 0)
        {
            Lost += lost;
            _logger.LogError(
                "TICKS LOST: {Count} pending tick(s) were trimmed from {Stream} by its length cap before they " +
                "could be stored (first {First}). {Total} lost since the worker started — the worker has been " +
                "failing for longer than the stream holds; see the errors before this line.",
                lost, _options.StreamName, claim.DeletedIds.Concat(trimmed).First(), Lost);
        }

        var present = claim.Entries.Where(x => !x.Trimmed).ToList();
        if (present.Count == 0) return lost;

        var deliveries = await _stream.DeliveryCountsAsync(present.Select(x => x.Id).ToList());

        var exhausted = new List<TickStreamEntry>();
        var lastTry = new List<TickStreamEntry>();
        var retry = new List<TickStreamEntry>();
        foreach (var entry in present)
        {
            // Not in the pending list any more: acknowledged since the claim.
            if (!deliveries.TryGetValue(entry.Id, out var count)) continue;

            // The count includes this delivery, so count - 1 tries have been made.
            if (count > _options.MaxDeliveries) exhausted.Add(entry);
            else if (count == _options.MaxDeliveries) lastTry.Add(entry);
            else retry.Add(entry);
        }

        foreach (var entry in exhausted)
        {
            await DeadLetterAsync(entry, $"not stored after {deliveries[entry.Id] - 1} deliveries",
                deliveries[entry.Id]);
        }

        if (retry.Count > 0)
        {
            var parsed = await ParseOrDeadLetterAsync(retry, deliveries);
            if (parsed.Count > 0 && await TryProcessAsync(parsed, redelivered: true, cancellationToken))
            {
                await AckStoredAsync(parsed);
            }
        }

        // The last try is made one entry at a time: whatever still fails is
        // the entry itself, and only it is given up on next time round.
        foreach (var entry in lastTry)
        {
            var parsed = await ParseOrDeadLetterAsync([entry], deliveries);
            if (parsed.Count > 0 && await TryProcessAsync(parsed, redelivered: true, cancellationToken))
            {
                await AckStoredAsync(parsed);
            }
        }

        return lost + present.Count;
    }

    private async Task<bool> TryProcessAsync(
        List<(TickStreamEntry Entry, MarketTickStreamMessage Message)> batch,
        bool redelivered,
        CancellationToken cancellationToken)
    {
        try
        {
            await _process(batch.Select(x => x.Message).ToList(), redelivered, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastPassFailed = true;
            _logger.LogError(
                ex,
                "Could not store {Count} tick(s) ({First}..{Last}){Redelivered}. They stay pending, are tried again " +
                "after {IdleMs} ms, and are dead-lettered to {DeadLetter} after {Max} deliveries.",
                batch.Count, batch[0].Entry.Id, batch[^1].Entry.Id, redelivered ? " on redelivery" : string.Empty,
                _options.ClaimMinIdleMs, _options.DeadLetterStreamName, _options.MaxDeliveries);
            return false;
        }
    }

    private async Task AckStoredAsync(List<(TickStreamEntry Entry, MarketTickStreamMessage Message)> batch)
    {
        await _stream.AckAsync(batch.Select(x => x.Entry.Id).ToList());
        Stored += batch.Count;
    }

    private Task<List<(TickStreamEntry Entry, MarketTickStreamMessage Message)>> ParseOrDeadLetterAsync(
        IReadOnlyList<TickStreamEntry> entries, long deliveries)
        => ParseOrDeadLetterAsync(entries, entries.ToDictionary(x => x.Id, _ => deliveries));

    /// <summary>
    /// The entries that hold a tick. The rest — no payload, or a payload that
    /// is not one — are dead-lettered and acknowledged: they used to be
    /// acknowledged and forgotten, which kept the stream moving but left no
    /// trace of what was thrown away.
    /// </summary>
    private async Task<List<(TickStreamEntry Entry, MarketTickStreamMessage Message)>> ParseOrDeadLetterAsync(
        IReadOnlyList<TickStreamEntry> entries, IReadOnlyDictionary<string, long> deliveries)
    {
        var parsed = new List<(TickStreamEntry, MarketTickStreamMessage)>(entries.Count);

        foreach (var entry in entries)
        {
            var count = deliveries.TryGetValue(entry.Id, out var c) ? c : 1;

            if (string.IsNullOrWhiteSpace(entry.Payload))
            {
                await DeadLetterAsync(entry, "empty payload", count);
                continue;
            }

            MarketTickStreamMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<MarketTickStreamMessage>(entry.Payload, _jsonOptions);
            }
            catch (Exception ex)
            {
                await DeadLetterAsync(entry, $"payload is not a tick: {ex.Message}", count);
                continue;
            }

            if (message is null)
            {
                await DeadLetterAsync(entry, "payload deserialized to null", count);
                continue;
            }

            // The moment the tick was published, when the feed did not say:
            // the entry's own clock, so a redelivery is recognised as the same
            // tick instead of being archived a second time under a new stamp.
            message.ReceivedUtc ??= StreamIdOrder.Time(entry.Id);

            parsed.Add((entry, message));
        }

        return parsed;
    }

    private async Task DeadLetterAsync(TickStreamEntry entry, string reason, long deliveries)
    {
        // Copied first, acknowledged second: a crash in between delivers it
        // again, which is a duplicate in the dead-letter stream — not a loss.
        await _stream.DeadLetterAsync(entry, reason, deliveries);
        await _stream.AckAsync([entry.Id]);
        DeadLettered++;

        _logger.LogError(
            "DEAD-LETTERED tick {Id} ({Reason}) to {DeadLetter}; {Total} since the worker started. " +
            "Inspect with: redis-cli XRANGE {DeadLetter} - +",
            entry.Id, reason, _options.DeadLetterStreamName, DeadLettered, _options.DeadLetterStreamName);
    }
}
