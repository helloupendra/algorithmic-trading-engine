namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// Closes the long-lived connections an account has open.
/// </summary>
/// <remarks>
/// The per-request cutoff (<see cref="ITokenValidityService"/>) ends a session for
/// the next request, but a websocket has already made its last one: it was
/// authorized once, at the handshake, and keeps what it was given until it
/// closes. Closed, it can only come back through that cutoff.
/// </remarks>
public interface ILiveConnectionCloser
{
    /// <summary>Closes every open connection of the account; returns how many.</summary>
    int CloseConnectionsOf(long userId);

    /// <summary>Closes only the connections opened by one login.</summary>
    int CloseConnectionsOfSession(Guid sessionId) => 0;
}
