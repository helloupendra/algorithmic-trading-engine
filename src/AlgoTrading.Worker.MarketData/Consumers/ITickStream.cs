// src/AlgoTrading.Worker.MarketData/Consumers/ITickStream.cs
namespace AlgoTrading.Worker.MarketData.Consumers;

/// <summary>One entry of the tick stream: its id and its payload.</summary>
/// <param name="Payload">The JSON tick, or null when the entry carries none.</param>
/// <param name="Trimmed">
/// True when Redis no longer holds the entry at all — the stream's length cap
/// trimmed it while it sat pending — so there is nothing left to store.
/// </param>
public sealed record TickStreamEntry(string Id, string? Payload, bool Trimmed = false);

/// <summary>What one claim of idle pending entries returned.</summary>
/// <param name="Entries">Entries now owned by this consumer, delivered once more.</param>
/// <param name="NextStartId">Where the next claim continues; "0-0" once the pending list has been walked.</param>
/// <param name="DeletedIds">Pending entries the stream had already trimmed: their ticks are gone.</param>
public sealed record TickStreamClaim(
    IReadOnlyList<TickStreamEntry> Entries,
    string NextStartId,
    IReadOnlyList<string> DeletedIds);

/// <summary>
/// The consumer-group operations the tick drain needs, and nothing else — so
/// its retry and dead-letter rules can be tested without a Redis server.
/// </summary>
public interface ITickStream
{
    /// <summary>Creates the consumer group if it does not exist yet.</summary>
    Task EnsureGroupAsync();

    /// <summary>Entries never delivered to this group before (XREADGROUP &gt;).</summary>
    Task<IReadOnlyList<TickStreamEntry>> ReadNewAsync(int count);

    /// <summary>Takes over entries unacknowledged for at least <paramref name="minIdleMs"/> (XAUTOCLAIM).</summary>
    Task<TickStreamClaim> ClaimIdleAsync(long minIdleMs, string startId, int count);

    /// <summary>How many times each entry has been delivered, from the pending list (XPENDING).</summary>
    Task<IReadOnlyDictionary<string, long>> DeliveryCountsAsync(IReadOnlyList<string> ids);

    Task AckAsync(IReadOnlyList<string> ids);

    /// <summary>Copies an entry to the dead-letter stream with the reason it could not be stored.</summary>
    Task DeadLetterAsync(TickStreamEntry entry, string reason, long deliveries);
}
