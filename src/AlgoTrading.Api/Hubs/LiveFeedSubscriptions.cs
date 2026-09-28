using System.Collections.Concurrent;

namespace AlgoTrading.Api.Hubs;

/// <summary>
/// Who is connected to the live feed hub, and which prices each connection
/// asked for.
/// </summary>
/// <remarks>
/// Until 28 Sep every tick batch went to every signed-in browser: a trader
/// with the Positions page open was sent every strike of every chain anyone
/// had subscribed, four hundred symbols a flush, to render three. Each
/// connection now names its symbols (<see cref="LiveFeedHub.Subscribe"/>)
/// and <see cref="LiveTickDispatcher"/> sends it those and nothing else.
/// <para>
/// A singleton keyed by SignalR connection id, emptied for a connection in
/// <see cref="LiveFeedHub.OnDisconnectedAsync"/>. Symbols are kept upper-cased
/// and trimmed, so "nse:nifty50-index" and "NSE:NIFTY50-INDEX " are one
/// subscription; the prices pushed still carry the feed's own spelling.
/// </para>
/// </remarks>
public sealed class LiveFeedSubscriptions
{
    /// <summary>
    /// The most symbols one connection may follow. An option chain at ±20
    /// strikes is 82 contracts; four of them with the watchlist fit well
    /// inside this, and a page asking for more is asking for the whole feed —
    /// which is what <see cref="LiveFeedHub.SubscribeAll"/> is for.
    /// </summary>
    public const int MaxSymbolsPerConnection = 400;

    private readonly ConcurrentDictionary<string, LiveFeedConnection> _connections = new(StringComparer.Ordinal);

    /// <summary>The key a symbol is subscribed and matched under, or null for a blank one.</summary>
    public static string? KeyOf(string? symbol)
        => string.IsNullOrWhiteSpace(symbol) ? null : symbol.Trim().ToUpperInvariant();

    /// <summary>Records a connection as it opens: whose it is, and whether they are an admin.</summary>
    public void Connect(string connectionId, long? userId, bool isAdmin)
        => _connections[connectionId] = new LiveFeedConnection(connectionId, userId, isAdmin);

    /// <summary>Forgets a connection and everything it subscribed.</summary>
    public void Disconnect(string connectionId) => _connections.TryRemove(connectionId, out _);

    /// <summary>How many connections are open.</summary>
    public int ConnectionCount => _connections.Count;

    /// <summary>
    /// Adds <paramref name="symbols"/> to the connection's set, all or none:
    /// a call that would take the set past <see cref="MaxSymbolsPerConnection"/>
    /// adds nothing and says so.
    /// </summary>
    public SubscribeResult Subscribe(string connectionId, IEnumerable<string?>? symbols)
    {
        var connection = Get(connectionId);
        var keys = Keys(symbols);
        return connection.Add(keys);
    }

    /// <summary>Removes <paramref name="symbols"/>; returns how many the connection still follows.</summary>
    public int Unsubscribe(string connectionId, IEnumerable<string?>? symbols)
        => Get(connectionId).Remove(Keys(symbols));

    /// <summary>Whether the connection receives every symbol, whatever it subscribed.</summary>
    public void SetAll(string connectionId, bool all) => Get(connectionId).All = all;

    /// <summary>How many symbols the connection follows (not counting a <see cref="SetAll"/>).</summary>
    public int CountFor(string connectionId)
        => _connections.TryGetValue(connectionId, out var connection) ? connection.Count : 0;

    /// <summary>Whether the connection receives every symbol.</summary>
    public bool IsAll(string connectionId)
        => _connections.TryGetValue(connectionId, out var connection) && connection.All;

    /// <summary>The open connections, as they are now.</summary>
    public IReadOnlyList<LiveFeedConnection> Snapshot()
    {
        var list = new List<LiveFeedConnection>(_connections.Count);
        foreach (var pair in _connections)
        {
            list.Add(pair.Value);
        }
        return list;
    }

    /// <summary>The open connections of admins.</summary>
    public IReadOnlyList<string> AdminConnectionIds()
    {
        var ids = new List<string>();
        foreach (var pair in _connections)
        {
            if (pair.Value.IsAdmin) ids.Add(pair.Key);
        }
        return ids;
    }

    // A method on a hub that the client calls before OnConnectedAsync has run
    // cannot happen, but a test (or a later refactor) may; it gets an entry
    // with nobody behind it rather than an exception.
    private LiveFeedConnection Get(string connectionId)
        => _connections.GetOrAdd(connectionId, id => new LiveFeedConnection(id, null, false));

    private static HashSet<string> Keys(IEnumerable<string?>? symbols)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (symbols is null) return keys;

        foreach (var symbol in symbols)
        {
            if (KeyOf(symbol) is { } key) keys.Add(key);
        }
        return keys;
    }
}

/// <param name="Accepted">False when the call would have gone past the cap; nothing was added.</param>
/// <param name="Count">How many symbols the connection follows now.</param>
public readonly record struct SubscribeResult(bool Accepted, int Count);

/// <summary>One open connection to the live feed hub.</summary>
public sealed class LiveFeedConnection
{
    private readonly object _gate = new();
    private readonly HashSet<string> _symbols = new(StringComparer.Ordinal);
    private volatile bool _all;

    public LiveFeedConnection(string connectionId, long? userId, bool isAdmin)
    {
        ConnectionId = connectionId;
        UserId = userId;
        IsAdmin = isAdmin;
    }

    public string ConnectionId { get; }

    /// <summary>The signed-in user, from the token; null only for an entry made without one.</summary>
    public long? UserId { get; }

    public bool IsAdmin { get; }

    /// <summary>Receives every symbol (<see cref="LiveFeedHub.SubscribeAll"/>).</summary>
    public bool All
    {
        get => _all;
        set => _all = value;
    }

    public int Count
    {
        get { lock (_gate) return _symbols.Count; }
    }

    internal SubscribeResult Add(HashSet<string> keys)
    {
        lock (_gate)
        {
            int added = 0;
            foreach (var key in keys)
            {
                if (!_symbols.Contains(key)) added++;
            }

            if (_symbols.Count + added > LiveFeedSubscriptions.MaxSymbolsPerConnection)
                return new SubscribeResult(false, _symbols.Count);

            _symbols.UnionWith(keys);
            return new SubscribeResult(true, _symbols.Count);
        }
    }

    internal int Remove(HashSet<string> keys)
    {
        lock (_gate)
        {
            _symbols.ExceptWith(keys);
            return _symbols.Count;
        }
    }

    /// <summary>
    /// Copies the entries of <paramref name="changed"/> this connection wants
    /// into <paramref name="into"/>, the later value winning; returns how many.
    /// </summary>
    /// <remarks>
    /// Walks whichever side is smaller: a Positions page follows a dozen
    /// symbols while a flush can carry hundreds, and a chain page is the
    /// other way round.
    /// </remarks>
    public int CopyWanted<T>(IReadOnlyDictionary<string, T> changed, IDictionary<string, T> into)
    {
        if (changed.Count == 0) return 0;

        if (_all)
        {
            foreach (var pair in changed)
            {
                into[pair.Key] = pair.Value;
            }
            return changed.Count;
        }

        int copied = 0;
        lock (_gate)
        {
            if (_symbols.Count <= changed.Count)
            {
                foreach (var key in _symbols)
                {
                    if (!changed.TryGetValue(key, out var value)) continue;
                    into[key] = value;
                    copied++;
                }
            }
            else
            {
                foreach (var pair in changed)
                {
                    if (!_symbols.Contains(pair.Key)) continue;
                    into[pair.Key] = pair.Value;
                    copied++;
                }
            }
        }
        return copied;
    }
}
