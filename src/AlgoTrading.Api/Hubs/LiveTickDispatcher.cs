using System.Text.Json.Serialization;
using AlgoTrading.Api.Configuration;
using AlgoTrading.Contracts.LiveData;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Hubs;

/// <summary>
/// One price as the hub pushes it in <c>ReceiveTicks</c>: the fields a screen
/// renders, and nothing else.
/// </summary>
/// <remarks>
/// The names are pinned rather than left to the protocol's naming policy: the
/// console reads exactly these eight, and they are the shape the broadcast had
/// before this type existed. <c>RawPayload</c> — the broker's whole message —
/// has no business on a websocket.
/// </remarks>
public sealed record LiveTickPush(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("lastTradedPrice")] decimal? LastTradedPrice,
    [property: JsonPropertyName("bidPrice")] decimal? BidPrice,
    [property: JsonPropertyName("askPrice")] decimal? AskPrice,
    [property: JsonPropertyName("volume")] long? Volume,
    [property: JsonPropertyName("openInterest")] long? OpenInterest,
    [property: JsonPropertyName("impliedVolatility")] decimal? ImpliedVolatility,
    [property: JsonPropertyName("exchangeTimestampUtc")] DateTime? ExchangeTimestampUtc)
{
    public static LiveTickPush From(UpsertLiveTickRequest tick) => new(
        tick.Symbol,
        tick.LastTradedPrice,
        tick.BidPrice,
        tick.AskPrice,
        tick.Volume,
        tick.OpenInterest,
        tick.ImpliedVolatility,
        tick.ExchangeTimestampUtc);
}

/// <summary>
/// Pushes live prices to browsers: the latest price of each symbol, at most
/// one message per connection per <see cref="LiveFeedOptions.PushIntervalMs"/>,
/// holding only the symbols that connection subscribed.
/// </summary>
/// <remarks>
/// <para>
/// The ingest path only enqueues (<see cref="Enqueue(IEnumerable{UpsertLiveTickRequest})"/>):
/// a dictionary write under a lock, never a network call. Sending from the
/// request itself — even fire-and-forget — put every browser's websocket one
/// step from the tick write, and on 2026-09-07 the feed the strategies trade
/// on was already falling a minute behind without that help.
/// </para>
/// <para>
/// Each flush takes what changed since the last one (the newest tick of a
/// symbol wins, by exchange stamp; see <see cref="Enqueue(IEnumerable{UpsertLiveTickRequest})"/>)
/// and gives every connection its share. A connection whose
/// previous message is still being written — a phone on a bad network, a tab
/// the browser has throttled — is not sent a second one on top: its share
/// waits, merged, until the first is through. So a slow browser holds at most
/// one price per symbol here, however long it stalls, and never delays the
/// others; a failed send is logged and forgotten.
/// </para>
/// </remarks>
public sealed class LiveTickDispatcher : BackgroundService
{
    /// <summary>The client method the prices arrive on.</summary>
    public const string ReceiveTicks = "ReceiveTicks";

    private readonly LiveFeedSubscriptions _subscriptions;
    private readonly IHubContext<LiveFeedHub> _hub;
    private readonly LiveFeedOptions _options;
    private readonly ILogger<LiveTickDispatcher> _logger;

    /// <summary>
    /// The most symbols whose last exchange stamp is remembered. A day's feed
    /// carries a few thousand; the map is emptied when it passes this, which
    /// costs one moment without the ordering rule rather than memory that
    /// grows with every expired contract until the API restarts.
    /// </summary>
    internal const int MaxStampedSymbols = 100_000;

    private readonly object _pendingGate = new();
    private Dictionary<string, LiveTickPush> _pending = new(StringComparer.Ordinal);

    // The newest exchange stamp queued for each symbol; touched only under _pendingGate.
    private readonly Dictionary<string, DateTime> _lastStamp = new(StringComparer.Ordinal);

    // Touched only inside Flush, which holds _flushGate.
    private readonly object _flushGate = new();
    private readonly Dictionary<string, Outbox> _outboxes = new(StringComparer.Ordinal);

    public LiveTickDispatcher(
        LiveFeedSubscriptions subscriptions,
        IHubContext<LiveFeedHub> hub,
        IOptions<LiveFeedOptions> options,
        ILogger<LiveTickDispatcher> logger)
    {
        _subscriptions = subscriptions;
        _hub = hub;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The interval between flushes, after the floor is applied.</summary>
    public TimeSpan PushInterval =>
        TimeSpan.FromMilliseconds(Math.Max(LiveFeedOptions.MinPushIntervalMs, _options.PushIntervalMs));

    /// <summary>Queues one tick for the next flush. Never throws.</summary>
    public void Enqueue(UpsertLiveTickRequest tick) => Enqueue(new[] { tick });

    /// <summary>
    /// Queues a batch of ticks for the next flush; a later tick of a symbol
    /// replaces an earlier one. Blank symbols are skipped. Never throws: the
    /// caller is storing prices, and a screen is not a reason to fail that.
    /// </summary>
    /// <remarks>
    /// "Later" is by exchange stamp, as <c>live_quotes_latest</c> decides it
    /// (LiveDataService, UpsertLatestQuoteAsync and ApplyLatestQuote): the
    /// feed posts from six threads at once, so at the open a batch holding
    /// 09:15:07 can arrive after one holding 09:15:08. The table refused the
    /// older price, but the last arrival won here and was pushed, and the
    /// Positions page, run cards and Desk marked to a price the backend had
    /// thrown away until that contract ticked again. A live tick older than
    /// one already queued for its symbol — pending, or pushed in an earlier
    /// flush — is dropped. A replay runs behind the live stamps on purpose and
    /// is never dropped; a tick without a stamp has no order to keep.
    /// </remarks>
    public void Enqueue(IEnumerable<UpsertLiveTickRequest> ticks)
    {
        try
        {
            // Built outside the lock, so the lock is held for dictionary work only.
            var batch = new List<(string Key, LiveTickPush Push, DateTime? Stamp, bool Replay)>();
            foreach (var tick in ticks)
            {
                if (tick is null || LiveFeedSubscriptions.KeyOf(tick.Symbol) is not { } key) continue;
                batch.Add((key, LiveTickPush.From(tick), tick.ExchangeTimestampUtc?.ToUniversalTime(), tick.IsReplay));
            }

            if (batch.Count == 0) return;

            lock (_pendingGate)
            {
                if (_lastStamp.Count > MaxStampedSymbols) _lastStamp.Clear();

                foreach (var (key, push, stamp, replay) in batch)
                {
                    if (stamp is { } at)
                    {
                        if (!replay && _lastStamp.TryGetValue(key, out var newest) && at < newest) continue;
                        _lastStamp[key] = at;
                    }

                    _pending[key] = push;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not queue ticks for the live feed hub.");
        }
    }

    /// <summary>
    /// One flush: what changed since the last one goes to the connections that
    /// want it, one message each. Returns how many messages were started. The
    /// sends are not awaited here; see the remarks on the class.
    /// </summary>
    public int Flush()
    {
        Dictionary<string, LiveTickPush>? changed = null;
        lock (_pendingGate)
        {
            if (_pending.Count > 0)
            {
                changed = _pending;
                _pending = new Dictionary<string, LiveTickPush>(StringComparer.Ordinal);
            }
        }

        lock (_flushGate)
        {
            var connections = _subscriptions.Snapshot();

            // A closed connection's backlog goes with it.
            if (_outboxes.Count > 0)
            {
                var open = new HashSet<string>(connections.Select(x => x.ConnectionId), StringComparer.Ordinal);
                foreach (var id in _outboxes.Keys.Where(id => !open.Contains(id)).ToList())
                {
                    _outboxes.Remove(id);
                }
            }

            int started = 0;
            foreach (var connection in connections)
            {
                if (!_outboxes.TryGetValue(connection.ConnectionId, out var outbox))
                {
                    if (changed is null) continue;
                    outbox = new Outbox();
                    if (connection.CopyWanted(changed, outbox.Pending) == 0) continue;
                    _outboxes[connection.ConnectionId] = outbox;
                }
                else if (changed is not null)
                {
                    connection.CopyWanted(changed, outbox.Pending);
                }

                if (outbox.Pending.Count == 0 || !outbox.InFlight.IsCompleted) continue;

                var ticks = outbox.Pending.Values.ToList();
                outbox.Pending.Clear();
                outbox.InFlight = SendAsync(connection.ConnectionId, ticks);
                started++;
            }

            return started;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    Flush();
                }
                catch (Exception ex)
                {
                    // A bad flush loses one interval's prices on screen; the
                    // next tick of each symbol puts it right.
                    _logger.LogWarning(ex, "Live feed flush failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>Sends one connection its prices. The task never faults.</summary>
    private async Task SendAsync(string connectionId, List<LiveTickPush> ticks)
    {
        try
        {
            await _hub.Clients.Client(connectionId).SendAsync(ReceiveTicks, ticks);
        }
        catch (Exception ex)
        {
            // A browser that went away mid-send is not a data problem; the
            // next flush simply does not include it.
            _logger.LogDebug(ex, "Could not push {Count} tick(s) to live feed connection {ConnectionId}.", ticks.Count, connectionId);
        }
    }

    /// <summary>What one connection is owed, and the send it is waiting on.</summary>
    private sealed class Outbox
    {
        public Dictionary<string, LiveTickPush> Pending { get; } = new(StringComparer.Ordinal);

        public Task InFlight { get; set; } = Task.CompletedTask;
    }
}
