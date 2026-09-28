using AlgoTrading.Api.Configuration;
using AlgoTrading.Application.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace AlgoTrading.Api.Hubs;

/// <summary>
/// How the API puts <see cref="LiveFeedHub"/> on the wire, in one place, so
/// the in-process tests register and map the hub exactly as the API does.
/// </summary>
public static class LiveFeedHubSetup
{
    /// <summary>The hub's address.</summary>
    public const string Path = "/hubs/livefeed";

    /// <summary>The category SignalR logs a failed hub call under ("fail: Failed to invoke hub method").</summary>
    public const string DispatcherLogCategory = "Microsoft.AspNetCore.SignalR.Internal.DefaultHubDispatcher";

    /// <summary>
    /// SignalR, the hub's state (who follows what, the coalescing
    /// dispatcher, desk events) and its logging. The dispatcher's clock is
    /// the caller's to start: the API adds it as a hosted service; a test
    /// flushes by hand.
    /// </summary>
    /// <remarks>
    /// A hub call that throws is logged by SignalR at Error, whatever it
    /// threw, and a page asking for more than 400 symbols is refused with a
    /// <see cref="HubException"/> the console shows as it is: an expected
    /// answer to a page, not a fault of the server, and it put "fail:" lines
    /// in the API log. SignalR's own line is turned off here and
    /// <see cref="LiveFeedHubLogging"/> writes the one it should have: a
    /// warning for a refusal, an error for anything else. The browser is
    /// sent the same error either way.
    /// </remarks>
    public static IServiceCollection AddLiveFeedHub(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSignalR(options => options.AddFilter<LiveFeedHubLogging>());
        services.Configure<LoggerFilterOptions>(options => options.AddFilter(DispatcherLogCategory, LogLevel.None));

        // Which symbols each browser asked for, and the dispatcher that pushes
        // each one its prices every LiveFeed:PushIntervalMs, coalesced.
        services.Configure<LiveFeedOptions>(configuration.GetSection(LiveFeedOptions.SectionName));
        services.AddSingleton<LiveFeedSubscriptions>();
        services.AddSingleton<LiveTickDispatcher>();

        // "Something on the desk changed" (an order, fill, run, risk, position
        // or carry), pushed over the same hub to the owner and the admins, so a
        // page fetches again when told instead of polling fast.
        services.AddSingleton<IDeskEventPublisher, SignalRDeskEventPublisher>();
        return services;
    }

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

/// <summary>
/// Logs a failed call on the live feed hub by what it was: a refusal the
/// hub meant (<see cref="HubException"/>) at Warning, anything else at Error.
/// See <see cref="LiveFeedHubSetup.AddLiveFeedHub"/>.
/// </summary>
public sealed class LiveFeedHubLogging(ILogger<LiveFeedHub> logger) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(invocationContext);
        }
        catch (HubException ex)
        {
            logger.LogWarning("Live feed hub refused {Method} on connection {ConnectionId}: {Reason}",
                invocationContext.HubMethodName, invocationContext.Context.ConnectionId, ex.Message);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Live feed hub method {Method} failed on connection {ConnectionId}.",
                invocationContext.HubMethodName, invocationContext.Context.ConnectionId);
            throw;
        }
    }
}
