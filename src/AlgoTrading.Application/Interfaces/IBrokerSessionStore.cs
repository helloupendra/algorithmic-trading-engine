using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// Interface for a store that persists and retrieves the active broker session (tokens, app IDs).
/// Typically backed by a database or a secure local file.
/// </summary>
public interface IBrokerSessionStore
{
    /// <summary>
    /// Retrieves the current active session, if one exists and is valid.
    /// </summary>
    Task<BrokerSession?> GetCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a new or updated session to the store.
    /// </summary>
    Task SaveAsync(BrokerSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the current session, effectively logging the user out.
    /// </summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}
