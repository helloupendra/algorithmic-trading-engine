using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// Interface for a store that persists and retrieves broker sessions (tokens, app IDs).
/// Typically backed by a database or a secure local file.
/// </summary>
/// <remarks>
/// Sessions are per connector. Connecting a second broker must never invalidate
/// the first one's token, so every lookup that matters takes a provider key;
/// <see cref="GetCurrentAsync"/> remains for the callers that legitimately want
/// "whatever is connected" — the console banner and the Python ingestor.
/// </remarks>
public interface IBrokerSessionStore
{
    /// <summary>
    /// The most recently updated active session of any connector.
    /// </summary>
    Task<BrokerSession?> GetCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The active session for one connector, or null when it is not connected.
    /// </summary>
    Task<BrokerSession?> GetForProviderAsync(
        string providerKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a new or updated session to the store.
    /// </summary>
    Task SaveAsync(BrokerSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deactivates sessions: one connector's when <paramref name="providerKey"/> is
    /// given, every connector's when it is null.
    /// </summary>
    Task ClearAsync(string? providerKey = null, CancellationToken cancellationToken = default);
}
