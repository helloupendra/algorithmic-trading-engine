using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AlgoTrading.Api.Hubs;

/// <summary>
/// The console's websocket: live prices for the symbols a page asked for, and
/// desk events that tell a page what to fetch again.
/// </summary>
/// <remarks>
/// <para>
/// Authorized. What travels over it is live market data the platform pays a
/// broker for, and it used to go to anyone who knew the URL.
/// </para>
/// <para>
/// Prices: a connection names its symbols (<see cref="Subscribe"/>) and gets
/// <c>ReceiveTicks</c> with those and no others, coalesced by
/// <see cref="LiveTickDispatcher"/>. Until 28 Sep every browser was sent every
/// tick of every symbol on the feed.
/// </para>
/// <para>
/// Desk events: each connection joins <c>user:{id}</c>, and an admin's also
/// <c>role:admin</c>; an order, fill, run, risk, position or carry change is
/// sent to its owner and to the admins as <c>DeskEvent</c>
/// (<see cref="SignalRDeskEventPublisher"/>).
/// </para>
/// </remarks>
[Authorize]
public class LiveFeedHub : Hub
{
    /// <summary>
    /// The query parameter, and its value, a console built from 28 Sep on
    /// connects with (<c>/hubs/livefeed?v=2</c>): it names its symbols with
    /// <see cref="Subscribe"/>. A connection without it is an older console.
    /// </summary>
    public const string ProtocolQueryKey = "v";

    /// <inheritdoc cref="ProtocolQueryKey"/>
    public const string CurrentProtocol = "2";

    private readonly LiveFeedSubscriptions _subscriptions;
    private readonly IUserAdminService _users;

    public LiveFeedHub(LiveFeedSubscriptions subscriptions, IUserAdminService users)
    {
        _subscriptions = subscriptions;
        _users = users;
    }

    /// <remarks>
    /// Admin, and the account being active at all, are read from the account
    /// row, not the token: a token is good for an hour, and an admin demoted
    /// ten minutes ago still carries the Admin claim. The token decided
    /// role:admin once, at connect, until 28 Sep, and the socket kept every
    /// account's desk events for as long as it stayed up. A connection now
    /// also closes when its token expires (<see cref="LiveFeedHubSetup"/>), so
    /// this runs again at least once an hour.
    /// </remarks>
    public override async Task OnConnectedAsync()
    {
        long? userId = Context.User?.GetUserId();
        var account = userId is { } uid ? await _users.GetAsync(uid, Context.ConnectionAborted) : null;
        if (userId is not { } id || account is null || !account.IsActive)
        {
            // The handshake's sign-out cutoff refuses a disabled account's
            // token already; this is for a row that changed since.
            Context.Abort();
            return;
        }

        bool admin = string.Equals(account.Role, UserRoles.Admin, StringComparison.Ordinal);
        _subscriptions.Connect(Context.ConnectionId, id, admin);

        await Groups.AddToGroupAsync(Context.ConnectionId, DeskEventGroups.User(id));

        if (admin)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, DeskEventGroups.Admins);
        }

        // TEMPORARY, added 28 Sep 2026 for one release; remove once no
        // console built before 28 Sep can still be open.
        //
        // A console built before 28 Sep never calls Subscribe: the API used to
        // send every tick to every browser, and it only listened. A tab left
        // open across the deploy reconnects to this hub and would be sent
        // nothing, silently, and it never reloads by itself (only a lazy
        // screen's missing chunk does that). It gets the old whole feed if its
        // account may have the whole feed at all, decided exactly as
        // SubscribeAll decides it; otherwise nothing, and it keeps its polls.
        if (!IsCurrentConsole())
        {
            var decision = await ModuleAccess.CheckAsync(Context.User, _users, PlatformModules.MarketData, Context.ConnectionAborted);
            if (decision == ModuleAccessDecision.Allowed) _subscriptions.SetAll(Context.ConnectionId, true);
        }

        await base.OnConnectedAsync();
    }

    /// <summary>Whether the connection said it is a console that subscribes (<see cref="CurrentProtocol"/>).</summary>
    private bool IsCurrentConsole()
        => string.Equals(Context.GetHttpContext()?.Request.Query[ProtocolQueryKey].ToString(), CurrentProtocol, StringComparison.Ordinal);

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // SignalR drops the connection from its groups by itself; the
        // subscriptions are ours to forget.
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
