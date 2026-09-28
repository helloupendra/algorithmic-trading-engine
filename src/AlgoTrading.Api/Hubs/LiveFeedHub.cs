using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AlgoTrading.Api.Hubs;

/// <summary>
/// The console's websocket: live prices for the symbols a page asked for.
/// </summary>
/// <remarks>
/// <para>
/// Authorized. What travels over it is live market data the platform pays a
/// broker for, and it used to go to anyone who knew the URL.
/// </para>
/// <para>
/// A connection names its symbols (<see cref="Subscribe"/>) and gets
/// <c>ReceiveTicks</c> with those and no others, coalesced by
/// <see cref="LiveTickDispatcher"/>. Until 28 Sep every browser was sent every
/// tick of every symbol on the feed.
/// </para>
/// </remarks>
[Authorize]
public class LiveFeedHub : Hub
{
    private readonly LiveFeedSubscriptions _subscriptions;
    private readonly IUserAdminService _users;

    public LiveFeedHub(LiveFeedSubscriptions subscriptions, IUserAdminService users)
    {
        _subscriptions = subscriptions;
        _users = users;
    }

    public override async Task OnConnectedAsync()
    {
        var user = Context.User;
        _subscriptions.Connect(Context.ConnectionId, user?.GetUserId(), user?.IsAdmin() == true);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _subscriptions.Disconnect(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Adds symbols to this connection's set. Returns how many it follows now.
    /// </summary>
    /// <remarks>
    /// Blanks are dropped and case does not matter. A call that would take the
    /// set past <see cref="LiveFeedSubscriptions.MaxSymbolsPerConnection"/>
    /// adds nothing and fails with a message the page can show.
    /// </remarks>
    public int Subscribe(string[]? symbols)
    {
        var result = _subscriptions.Subscribe(Context.ConnectionId, symbols);
        if (!result.Accepted)
        {
            throw new HubException(
                $"A page may follow at most {LiveFeedSubscriptions.MaxSymbolsPerConnection} symbols; this one follows "
                + $"{result.Count} and the new ones would take it past that. Unsubscribe from some first.");
        }

        return result.Count;
    }

    /// <summary>Removes symbols from this connection's set. Returns how many it still follows.</summary>
    public int Unsubscribe(string[]? symbols) => _subscriptions.Unsubscribe(Context.ConnectionId, symbols);

    /// <summary>
    /// Every symbol on the feed, for the pages that show the whole feed (the
    /// recording list, the data health screens). False, and nothing changes,
    /// for an account without the market-data module.
    /// </summary>
    /// <remarks>
    /// Decided exactly as the market-data endpoints decide it
    /// (<see cref="ModuleAccess"/>): the whole feed is what those endpoints
    /// serve, and a websocket must not be a way around them.
    /// </remarks>
    public async Task<bool> SubscribeAll()
    {
        var decision = await ModuleAccess.CheckAsync(Context.User, _users, PlatformModules.MarketData, Context.ConnectionAborted);
        if (decision != ModuleAccessDecision.Allowed) return false;

        _subscriptions.SetAll(Context.ConnectionId, true);
        return true;
    }

    /// <summary>Back to only the symbols this connection subscribed.</summary>
    public bool UnsubscribeAll()
    {
        _subscriptions.SetAll(Context.ConnectionId, false);
        return true;
    }
}
