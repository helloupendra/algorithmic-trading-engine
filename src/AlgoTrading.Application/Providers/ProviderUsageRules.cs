using System.Globalization;

namespace AlgoTrading.Application.Providers;

/// <summary>Where one kind of data from a connector stands right now.</summary>
public enum UsageState
{
    /// <summary>Data of this kind arrived from the connector inside its freshness window.</summary>
    On,

    /// <summary>Nothing is arriving, and nothing should be: the markets are closed, or the connector is on standby.</summary>
    Idle,

    /// <summary>Nothing is arriving and something is known to be wrong or switched off.</summary>
    Off,

    /// <summary>The question could not be answered. Never shown as Off: not known is not no.</summary>
    Unknown,

    /// <summary>The connector does not declare this kind of data.</summary>
    NotOffered,
}

/// <summary>One item's answer: its state, a line saying why, and when its newest datum landed.</summary>
public sealed record UsageVerdict(UsageState State, string Summary, DateTime? LastUtc = null);

/// <summary>Whether each exchange group is trading. Null means the session could not be read.</summary>
public sealed record UsageMarkets(bool? NseOpen, string? NseHoliday, bool? McxOpen, string? McxHoliday);

/// <summary>A session token as stored. <see cref="Active"/> means a token exists and was not revoked.</summary>
public sealed record UsageToken(bool Active, DateTime IssuedUtc, DateTime? ExpiresUtc);

/// <summary>What is known about signing in. Null credentials, or <c>TokenKnown</c> false, means the read failed.</summary>
public sealed record UsageSessionFacts(
    ProviderAuthKind Auth,
    bool? CredentialsSaved,
    bool TokenKnown,
    UsageToken? Token);

/// <summary>The newest heartbeat row a feed wrote.</summary>
public sealed record UsageHeartbeat(
    string SourceName,
    string Status,
    DateTime LastUtc,
    int SubscribedCount,
    bool IsRecap,
    string? LastError);

/// <summary>What is known about a connector's live feed. Null values are reads that failed.</summary>
public sealed record UsageFeedFacts(
    bool HasFeed,
    bool? IsRunning,
    bool HeartbeatKnown,
    UsageHeartbeat? Heartbeat,
    long? TicksLastMinute,
    DateTime? LastTickUtc);

/// <summary>Latest-quote rows written by the connector, counted inside the quote window.</summary>
public sealed record UsageQuoteFacts(
    long Total,
    long Fresh,
    long FreshWithDepth,
    long FreshWithOpenInterest,
    long FreshWithGreeks,
    DateTime? NewestUtc);

/// <summary>
/// Option-chain snapshots written by the connector inside the chain window.
/// </summary>
/// <param name="Covered">
/// False when the bounded read did not reach back to the start of the window, so
/// finding nothing proves nothing.
/// </param>
public sealed record UsageChainFacts(
    bool Covered,
    DateTime? LatestUtc,
    int RowsInLatest,
    IReadOnlyList<string> Underlyings,
    long RowsWithOpenInterest,
    long RowsWithGreeks);

/// <summary>The newest bar written with this connector as its source, among the rows the bounded read covers.</summary>
public sealed record UsageCandleFacts(DateTime? NewestBarUtc, string? Resolution, int RowsExamined);

/// <summary>Vendor symbol rows kept for a connector that has its own symbol grammar.</summary>
public sealed record UsageInstrumentFacts(long Count, DateTime? LastUpdatedUtc);

/// <summary>
/// The rules behind a connector page's "What we take from this vendor" panel:
/// which state each kind of data is in, and the sentence that says why.
/// </summary>
/// <remarks>
/// Kept apart from the queries so they can be tested without a database, and
/// free of vendor names so every connector is judged by the same rules.
/// <para>
/// Three rules carry the weight. A kind of data the connector does not declare
/// is <see cref="UsageState.NotOffered"/>. A read that failed is
/// <see cref="UsageState.Unknown"/>, never <see cref="UsageState.Off"/>: on
/// 2026-09-11 a "not known yet" rendered as a fact cost a morning. And silence
/// while every market the connector covers is closed is
/// <see cref="UsageState.Idle"/>, because a holiday reported as an outage
/// teaches the operator to ignore the page.
/// </para>
/// </remarks>
public static class ProviderUsageRules
{
    /// <summary>Ticks counted over this window read as "flowing".</summary>
    public static readonly TimeSpan TickWindow = TimeSpan.FromSeconds(60);

    /// <summary>How far back the newest tick is looked for. Bounded: live_ticks is large.</summary>
    public static readonly TimeSpan TickLookback = TimeSpan.FromMinutes(5);

    /// <summary>A latest-quote row updated inside this window is fresh.</summary>
    public static readonly TimeSpan QuoteWindow = TimeSpan.FromMinutes(2);

    /// <summary>A chain capture inside this window is fresh. Chain pollers run slower than ticks.</summary>
    public static readonly TimeSpan ChainWindow = TimeSpan.FromMinutes(10);

    /// <summary>The same threshold the ingestor status endpoint uses for a healthy heartbeat.</summary>
    public static readonly TimeSpan HeartbeatFreshness = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan IstOffset = TimeSpan.FromMinutes(330);
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The state as the API writes it.</summary>
    public static string Wire(UsageState state) => state switch
    {
        UsageState.On => "on",
        UsageState.Idle => "idle",
        UsageState.Off => "off",
        UsageState.NotOffered => "not-offered",
        _ => "unknown",
    };

    /// <summary>
    /// The core decision for anything that flows.
    /// </summary>
    /// <param name="offered">The connector declares this kind of data.</param>
    /// <param name="fresh">Data arrived inside the window; null when the read failed.</param>
    /// <param name="producerRunning">
    /// Whether the process that would write it is running; null when that is not
    /// known or no single process is responsible.
    /// </param>
    /// <param name="marketsOpen">Whether any market the connector covers is trading; null when unknown.</param>
    public static UsageState Classify(bool offered, bool? fresh, bool? producerRunning, bool? marketsOpen)
    {
        if (!offered) return UsageState.NotOffered;
        if (fresh is null) return UsageState.Unknown;
        if (fresh == true) return UsageState.On;

        // A producer known to be stopped is off whatever the clock says: that is
        // a switch someone can flip, not the market resting.
        if (producerRunning == false) return UsageState.Off;
        if (marketsOpen == false) return UsageState.Idle;

        // Silent, and whether it ought to be flowing cannot be told.
        if (marketsOpen is null) return UsageState.Unknown;

        return UsageState.Off;
    }

    // ------------------------------------------------------------------
    // Markets
    // ------------------------------------------------------------------

    /// <summary>
    /// Which exchange groups a connector covers, from its declared segments.
    /// Cash, equity derivatives and currency trade NSE hours; MCX its own. A
    /// connector that declares no segments is assumed to cover both.
    /// </summary>
    public static (bool Nse, bool Mcx) MarketsCovered(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0) return (true, true);

        bool nse = segments.Any(s => s.Trim().ToUpperInvariant() is "CM" or "FO" or "CD");
        bool mcx = segments.Any(s => s.Trim().ToUpperInvariant() is "MCX" or "COM");

        return nse || mcx ? (nse, mcx) : (true, true);
    }

    /// <summary>True when any covered market trades, false when all are known closed, null otherwise.</summary>
    public static bool? AnyCoveredMarketOpen(UsageMarkets markets, IReadOnlyList<string> segments)
    {
        var (nse, mcx) = MarketsCovered(segments);
        var answers = new List<bool?>();
        if (nse) answers.Add(markets.NseOpen);
        if (mcx) answers.Add(markets.McxOpen);

        if (answers.Any(a => a == true)) return true;
        if (answers.All(a => a == false)) return false;
        return null;
    }

    /// <summary>"NSE closed for Ganesh Chaturthi, MCX closed" — the covered markets that are shut.</summary>
    public static string ClosedReason(UsageMarkets markets, IReadOnlyList<string> segments)
    {
        var (nse, mcx) = MarketsCovered(segments);
        bool nseClosed = nse && markets.NseOpen == false;
        bool mcxClosed = mcx && markets.McxOpen == false;

        // One exchange calendar holiday usually closes both; say it once.
        if (nseClosed && mcxClosed && string.Equals(markets.NseHoliday, markets.McxHoliday, StringComparison.OrdinalIgnoreCase))
        {
            return Closed("NSE and MCX", markets.NseHoliday);
        }

        var parts = new List<string>();
        if (nseClosed) parts.Add(Closed("NSE", markets.NseHoliday));
        if (mcxClosed) parts.Add(Closed("MCX", markets.McxHoliday));

        return parts.Count > 0 ? string.Join(", ", parts) : "markets closed";

        static string Closed(string name, string? holiday)
            => string.IsNullOrWhiteSpace(holiday) ? $"{name} closed" : $"{name} closed for {holiday}";
    }

    // ------------------------------------------------------------------
    // Feeds
    // ------------------------------------------------------------------

    /// <summary>
    /// The heartbeat names a connector's feed reports under. Every feed writes
    /// "python-&lt;key&gt;-feed", or "-recap" while it replays a session; the one
    /// feed that predates the others keeps the name it always had.
    /// </summary>
    /// <param name="providerKey">The connector key.</param>
    /// <param name="isLegacyIngestor">True for the feed run by the original ingestor supervisor.</param>
    public static IReadOnlyList<string> HeartbeatSourceNames(string providerKey, bool isLegacyIngestor)
    {
        string key = providerKey.Trim().ToLowerInvariant();
        var names = new List<string> { $"python-{key}-feed", $"python-{key}-recap" };
        if (isLegacyIngestor) names.Insert(0, LegacyIngestorSourceName);
        return names;
    }

    /// <summary>The heartbeat name of the original ingestor, written by the Python engine's config.</summary>
    public const string LegacyIngestorSourceName = "python-live-ingestor";

    /// <summary>True when a heartbeat name marks a replay rather than the live market.</summary>
    public static bool IsRecapSourceName(string sourceName)
        => sourceName.EndsWith("-recap", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a connector's feed is running: the API sees its process, or it is
    /// heartbeating (a feed the API did not start still writes). False only when
    /// both answers are in and both say no; null for no feed or a failed read.
    /// </summary>
    public static bool? FeedRunning(UsageFeedFacts feed, DateTime nowUtc)
    {
        if (!feed.HasFeed) return null;
        if (feed.IsRunning == true || (feed.HeartbeatKnown && HeartbeatHealthy(feed.Heartbeat, nowUtc))) return true;
        return feed.IsRunning == false && feed.HeartbeatKnown ? false : null;
    }

    public static bool HeartbeatHealthy(UsageHeartbeat? heartbeat, DateTime nowUtc)
        => heartbeat is not null
           && string.Equals(heartbeat.Status, "Running", StringComparison.OrdinalIgnoreCase)
           && nowUtc - heartbeat.LastUtc <= HeartbeatFreshness;

    // ------------------------------------------------------------------
    // Items
    // ------------------------------------------------------------------

    /// <param name="dataArriving">
    /// Ticks or quotes from this connector inside their freshness window. A daily
    /// sign-in connector can also run on a token pasted into configuration; when
    /// its data is arriving, "not connected" would be false.
    /// </param>
    public static UsageVerdict Session(UsageSessionFacts facts, DateTime nowUtc, bool dataArriving = false)
    {
        if (facts.Auth == ProviderAuthKind.None)
        {
            return new UsageVerdict(UsageState.On, "no login needed");
        }

        var token = facts.Token;
        bool usable = token is { Active: true } && (token.ExpiresUtc is null || nowUtc < token.ExpiresUtc);

        if (facts.Auth == ProviderAuthKind.ApiKey)
        {
            return facts.CredentialsSaved switch
            {
                null => new UsageVerdict(UsageState.Unknown, "saved credentials could not be read"),
                false => new UsageVerdict(UsageState.Off, "credentials not saved — it cannot sign in"),
                true => new UsageVerdict(
                    UsageState.On,
                    usable
                        ? $"signs in automatically · token issued {Age(token!.IssuedUtc, nowUtc)}"
                        : "signs in automatically with the saved credentials",
                    usable ? token!.IssuedUtc : null),
            };
        }

        // A daily browser sign-in: the token is the whole story.
        if (!facts.TokenKnown)
        {
            return new UsageVerdict(UsageState.Unknown, "session could not be read");
        }

        if (usable)
        {
            string expiry = token!.ExpiresUtc is { } expires
                ? $"expires {Clock(expires, nowUtc)}"
                : "expiry not known";
            return new UsageVerdict(
                UsageState.On,
                $"connected · signed in {Age(token.IssuedUtc, nowUtc)} · {expiry}",
                token.IssuedUtc);
        }

        if (dataArriving)
        {
            return new UsageVerdict(
                UsageState.On,
                token is { Active: true, ExpiresUtc: not null }
                    ? "sign-in expired, but data is arriving on a configured token · sign in for a full day"
                    : "no sign-in today, but data is arriving on a configured token · sign in for a full day");
        }

        if (token is { Active: true, ExpiresUtc: { } expired })
        {
            return new UsageVerdict(
                UsageState.Off,
                $"token expired {Clock(expired, nowUtc)} — sign in again",
                token.IssuedUtc);
        }

        return new UsageVerdict(
            UsageState.Off,
            facts.CredentialsSaved == false ? "not connected · credentials not saved" : "not connected");
    }

    public static UsageVerdict LiveTicks(
        bool offered,
        UsageFeedFacts feed,
        UsageMarkets markets,
        IReadOnlyList<string> segments,
        DateTime nowUtc)
    {
        if (!offered) return NotOffered();

        if (!feed.HasFeed)
        {
            return new UsageVerdict(UsageState.Off, "no live feed is registered for this connector");
        }

        var beat = feed.HeartbeatKnown ? feed.Heartbeat : null;
        bool beating = HeartbeatHealthy(beat, nowUtc);
        bool? running = FeedRunning(feed, nowUtc);

        bool? fresh = feed.TicksLastMinute is { } count ? count > 0 : null;
        bool? open = AnyCoveredMarketOpen(markets, segments);
        var state = Classify(true, fresh, running, open);

        string symbols = beat is { SubscribedCount: > 0 } ? $"{Count(beat.SubscribedCount)} symbols" : string.Empty;
        string recap = beat is { IsRecap: true } && beating ? "replaying a recorded session" : string.Empty;
        string lastTick = feed.LastTickUtc is { } at ? $"last tick {Age(at, nowUtc)}" : string.Empty;
        string heartbeat = beat is not null ? $"heartbeat {Age(beat.LastUtc, nowUtc)}" : "no heartbeat on record";

        string summary = state switch
        {
            UsageState.On => Join(
                symbols,
                lastTick,
                $"{Count(feed.TicksLastMinute!.Value)} ticks in the last minute",
                recap,
                feed.IsRunning == false ? "process not started by this API" : string.Empty),

            UsageState.Idle => Join(
                running == true ? "running" : string.Empty,
                symbols.Length > 0 ? $"{symbols} subscribed" : string.Empty,
                $"no ticks while {ClosedReason(markets, segments)}"),

            UsageState.Off when running == false => Join(
                "feed stopped",
                lastTick,
                beat is not null && !string.IsNullOrWhiteSpace(beat.LastError)
                    && !string.Equals(beat.Status, "Running", StringComparison.OrdinalIgnoreCase)
                    ? $"{beat.Status.ToLowerInvariant()}: {beat.LastError}"
                    : string.Empty),

            UsageState.Off => Join(
                running == true ? "running but no ticks in the last minute" : "no ticks in the last minute",
                symbols.Length > 0 ? $"{symbols} subscribed" : string.Empty,
                heartbeat),

            _ => fresh is null
                ? Join("tick count could not be read", running == true ? "feed running" : string.Empty, symbols)
                : Join("no ticks in the last minute", "market hours could not be read"),
        };

        return new UsageVerdict(state, summary, feed.LastTickUtc);
    }

    public static UsageVerdict Quotes(
        bool offered,
        UsageQuoteFacts? quotes,
        bool? feedRunning,
        bool? marketsOpen,
        string closedReason,
        DateTime nowUtc)
    {
        if (!offered) return NotOffered();
        if (quotes is null) return new UsageVerdict(UsageState.Unknown, "quote rows could not be read");

        var state = Classify(true, quotes.Fresh > 0, feedRunning, marketsOpen);
        string window = Minutes(QuoteWindow);
        string newest = quotes.NewestUtc is { } at ? $"newest {Age(at, nowUtc)}" : string.Empty;

        string summary = state switch
        {
            UsageState.On => Join($"{Count(quotes.Fresh)} of {Count(quotes.Total)} quotes updated in the last {window}", newest),
            UsageState.Idle => Join(HeldQuotes(quotes, window), closedReason),
            UsageState.Unknown => Join(HeldQuotes(quotes, window), "market hours could not be read"),
            _ => Join(
                quotes.Total == 0
                    ? "no quotes from this source"
                    : Join($"none of {Count(quotes.Total)} quotes updated in the last {window}", newest),
                feedRunning == false ? "feed stopped" : string.Empty),
        };

        return new UsageVerdict(state, summary, quotes.NewestUtc);
    }

    public static UsageVerdict Depth(
        bool offered,
        UsageQuoteFacts? quotes,
        bool? feedRunning,
        bool? marketsOpen,
        string closedReason)
    {
        if (!offered)
        {
            return NotOffered(quotes is { FreshWithDepth: > 0 }
                ? $"{Count(quotes.FreshWithDepth)} fresh quotes from it carry bid/ask sizes"
                : null);
        }

        if (quotes is null) return new UsageVerdict(UsageState.Unknown, "quote rows could not be read");

        var state = Classify(true, quotes.FreshWithDepth > 0, feedRunning, marketsOpen);
        string window = Minutes(QuoteWindow);

        string summary = state switch
        {
            UsageState.On => $"{Count(quotes.FreshWithDepth)} of {Count(quotes.Fresh)} fresh quotes carry bid/ask sizes",
            UsageState.Idle => Join($"no quotes updated in the last {window}", closedReason),
            UsageState.Unknown => Join($"no quotes updated in the last {window}", "market hours could not be read"),
            _ => Join(
                quotes.Fresh > 0
                    ? $"{Count(quotes.Fresh)} fresh quotes, none with bid/ask sizes"
                    : $"no quotes updated in the last {window}",
                feedRunning == false ? "feed stopped" : string.Empty),
        };

        return new UsageVerdict(state, summary);
    }

    public static UsageVerdict OpenInterest(
        bool offered,
        UsageQuoteFacts? quotes,
        UsageChainFacts? chain,
        bool? marketsOpen,
        string closedReason)
        => Derived(
            offered,
            what: "open interest",
            quoteRows: quotes?.FreshWithOpenInterest,
            quotesRead: quotes is not null,
            chainRows: chain?.RowsWithOpenInterest,
            chain: chain,
            marketsOpen: marketsOpen,
            closedReason: closedReason);

    public static UsageVerdict Greeks(
        bool offered,
        UsageQuoteFacts? quotes,
        UsageChainFacts? chain,
        bool? marketsOpen,
        string closedReason)
        => Derived(
            offered,
            what: "greeks",
            quoteRows: quotes?.FreshWithGreeks,
            quotesRead: quotes is not null,
            chainRows: chain?.RowsWithGreeks,
            chain: chain,
            marketsOpen: marketsOpen,
            closedReason: closedReason);

    /// <summary>
    /// Open interest and greeks reach the platform on quotes or on chain
    /// snapshots, so either source counts and both must be read to say "none".
    /// </summary>
    private static UsageVerdict Derived(
        bool offered,
        string what,
        long? quoteRows,
        bool quotesRead,
        long? chainRows,
        UsageChainFacts? chain,
        bool? marketsOpen,
        string closedReason)
    {
        var seen = new List<string>();
        if (quoteRows is > 0) seen.Add($"{Count(quoteRows.Value)} quotes in the last {Minutes(QuoteWindow)}");
        if (chainRows is > 0) seen.Add($"{Count(chainRows.Value)} chain rows in the last {Minutes(ChainWindow)}");

        if (!offered)
        {
            return NotOffered(seen.Count > 0 ? $"{string.Join(" and ", seen)} from it carry {what}" : null);
        }

        // Chain rows only answer when the chain read covered the whole window.
        bool? chainFresh = chain is null ? null : chainRows > 0 ? true : chain.Covered ? false : null;
        bool? quoteFresh = quotesRead ? quoteRows > 0 : null;

        bool? fresh = quoteFresh == true || chainFresh == true
            ? true
            : quoteFresh is null || chainFresh is null ? null : false;

        // Two producers may write these, so no single process being down makes it Off.
        var state = Classify(true, fresh, producerRunning: null, marketsOpen);
        string window = Minutes(ChainWindow);

        string summary = state switch
        {
            UsageState.On => $"{string.Join(" and ", seen)} carry {what}",
            UsageState.Idle => Join($"no {what} in the last {window}", closedReason),
            UsageState.Off => $"no {what} from this source in the last {window}",
            _ => fresh is null
                ? $"{what} could not be checked: {(quoteFresh is null ? "quote rows could not be read" : "chain snapshots could not be fully read")}"
                : Join($"no {what} in the last {window}", "market hours could not be read"),
        };

        return new UsageVerdict(state, summary);
    }

    public static UsageVerdict OptionChain(
        bool offered,
        UsageChainFacts? chain,
        bool? marketsOpen,
        string closedReason,
        DateTime nowUtc)
    {
        if (!offered)
        {
            return NotOffered(chain?.LatestUtc is { } seen
                ? $"it wrote a chain capture {Age(seen, nowUtc)}"
                : null);
        }

        if (chain is null) return new UsageVerdict(UsageState.Unknown, "chain snapshots could not be read");

        bool? fresh = chain.LatestUtc is not null ? true : chain.Covered ? false : null;
        var state = Classify(true, fresh, producerRunning: null, marketsOpen);
        string window = Minutes(ChainWindow);

        string summary = state switch
        {
            UsageState.On => Join(
                $"last capture {Age(chain.LatestUtc!.Value, nowUtc)}",
                $"{Count(chain.RowsInLatest)} rows in it",
                chain.Underlyings.Count > 0 ? string.Join(", ", chain.Underlyings) : string.Empty),
            UsageState.Idle => Join($"no capture in the last {window}", closedReason),
            UsageState.Off => $"no capture from this source in the last {window}",
            _ => fresh is null
                ? $"more chain rows were written in the last {window} than this check reads; could not tell"
                : Join($"no capture in the last {window}", "market hours could not be read"),
        };

        return new UsageVerdict(state, summary, chain.LatestUtc);
    }

    /// <summary>
    /// History is asked for on demand, so it has no freshness window: what
    /// matters is whether the router sends the requests here.
    /// </summary>
    /// <param name="chainPosition">0 for primary, n for fallback #n, -1 when not in the chain, null when routing failed.</param>
    /// <param name="primaryName">Who is asked first, for a fallback's summary.</param>
    /// <param name="candles">The newest bar stored from this source; null when the read failed.</param>
    public static UsageVerdict History(
        bool offered,
        int? chainPosition,
        string? primaryName,
        UsageCandleFacts? candles,
        DateTime nowUtc)
    {
        if (!offered) return NotOffered();

        string stored = candles is null
            ? "stored bars could not be read"
            : candles.NewestBarUtc is { } bar
                ? $"newest stored bar {Clock(bar, nowUtc)}{(string.IsNullOrWhiteSpace(candles.Resolution) ? string.Empty : $" ({ResolutionLabel(candles.Resolution)})")}"
                : candles.RowsExamined == 0
                    ? "no bars stored yet"
                    : $"none of the latest {Count(candles.RowsExamined)} stored bars came from it";

        var (state, routing) = chainPosition switch
        {
            null => (UsageState.Unknown, "history routing could not be resolved"),
            0 => (UsageState.On, "primary source for history"),
            > 0 => (UsageState.Idle, string.IsNullOrWhiteSpace(primaryName)
                ? $"fallback #{chainPosition} — asked only when the sources ahead of it cannot answer"
                : $"fallback #{chainPosition} — asked only when {primaryName} cannot answer"),
            _ => (UsageState.Off, "not routed — nothing asks this connector for history"),
        };

        return new UsageVerdict(state, Join(routing, stored), candles?.NewestBarUtc);
    }

    public static UsageVerdict Instruments(bool usesCanonicalSymbols, UsageInstrumentFacts? facts, DateTime nowUtc)
    {
        if (usesCanonicalSymbols)
        {
            return new UsageVerdict(UsageState.NotOffered, "uses the platform's symbols as they are — nothing to map");
        }

        if (facts is null) return new UsageVerdict(UsageState.Unknown, "symbol table could not be read");

        return facts.Count > 0
            ? new UsageVerdict(
                UsageState.On,
                Join($"{Count(facts.Count)} symbols mapped", facts.LastUpdatedUtc is { } at ? $"updated {Age(at, nowUtc)}" : string.Empty),
                facts.LastUpdatedUtc)
            : new UsageVerdict(UsageState.Off, "no symbols imported — its names for contracts cannot be translated");
    }

    public static UsageVerdict Orders(bool offered)
        => offered
            ? new UsageVerdict(UsageState.Idle, "available, but the platform places no orders through it today")
            : NotOffered();

    // ------------------------------------------------------------------
    // Wording
    // ------------------------------------------------------------------

    /// <summary>"4 s ago", "12 min ago", "3 h ago", "2 d ago". A future stamp reads "just now".</summary>
    public static string Age(DateTime utc, DateTime nowUtc)
    {
        var age = nowUtc - utc;
        if (age < TimeSpan.FromSeconds(1)) return "just now";
        if (age < TimeSpan.FromMinutes(1)) return $"{(int)age.TotalSeconds} s ago";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
        return $"{(int)age.TotalDays} d ago";
    }

    /// <summary>"06:00 IST" on the same IST day as now, "15 Sep 06:00 IST" otherwise.</summary>
    public static string Clock(DateTime utc, DateTime nowUtc)
    {
        var at = DateTime.SpecifyKind(utc, DateTimeKind.Utc) + IstOffset;
        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc) + IstOffset;

        return at.Date == now.Date
            ? $"{at.ToString("HH:mm", Invariant)} IST"
            : $"{at.ToString("d MMM HH:mm", Invariant)} IST";
    }

    public static string Count(long value) => value.ToString("N0", Invariant);

    /// <summary>"1" is "1 min", "D" is "daily"; anything else is shown as stored.</summary>
    public static string ResolutionLabel(string resolution)
    {
        string r = resolution.Trim();
        if (int.TryParse(r, NumberStyles.None, Invariant, out int minutes)) return $"{minutes} min";
        return r.ToUpperInvariant() is "D" or "1D" ? "daily" : r;
    }

    private static string Minutes(TimeSpan window) => $"{(int)window.TotalMinutes} min";

    private static string HeldQuotes(UsageQuoteFacts quotes, string window)
        => quotes.Total == 0
            ? "no quotes from this source"
            : $"{Count(quotes.Total)} quotes held, none updated in the last {window}";

    private static UsageVerdict NotOffered(string? observed = null)
        => new(UsageState.NotOffered, observed is null ? "not offered by this connector" : $"not declared by this connector, yet {observed}");

    private static string Join(params string[] parts)
        => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
