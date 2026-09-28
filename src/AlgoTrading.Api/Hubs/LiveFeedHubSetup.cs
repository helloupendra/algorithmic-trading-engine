namespace AlgoTrading.Api.Hubs;

/// <summary>
/// How the API puts <see cref="LiveFeedHub"/> on the wire, in one place, so
/// the in-process tests map the hub exactly as the API does.
/// </summary>
public static class LiveFeedHubSetup
{
    /// <summary>The hub's address.</summary>
    public const string Path = "/hubs/livefeed";

    /// <summary>
    /// Signed-in callers only, and a connection lives no longer than the token
    /// it opened with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Anonymous, this hub shipped the broker's full raw tick payload to anyone
    /// who had the URL, including every browser sitting on the public landing
    /// and login pages.
    /// </para>
    /// <para>
    /// The token is checked once, at the handshake. Until 28 Sep an open socket
    /// was never checked again: an admin demoted, disabled or signed out kept
    /// the role:admin group, and every account's desk events with it, for as
    /// long as the socket stayed up, hours past the 60-minute token. Closed
    /// when the token expires, the console reconnects with a fresh one, the
    /// sign-out cutoff is applied to it at the handshake, and
    /// <see cref="LiveFeedHub.OnConnectedAsync"/> reads the account row again.
    /// </para>
    /// </remarks>
    public static HubEndpointConventionBuilder MapLiveFeedHub(this IEndpointRouteBuilder endpoints)
        => endpoints
            .MapHub<LiveFeedHub>(Path, options => options.CloseOnAuthenticationExpiration = true)
            .RequireAuthorization();
}
