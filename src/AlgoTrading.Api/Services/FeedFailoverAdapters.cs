// src/AlgoTrading.Api/Services/FeedFailoverAdapters.cs

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Providers.Dhan;
using AlgoTrading.Infrastructure.Providers.Fyers;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace AlgoTrading.Api.Services;

/// <summary>
/// The newest live tick per exchange group, from the head of Redis
/// <c>market:ticks</c> — the stream every strategy reads.
/// </summary>
/// <remarks>
/// The stream rather than the tables, because it is what the strategies see:
/// <c>live_quotes_latest</c> keeps every symbol's last price forever (on
/// 10 Sep it showed 127 "prices" from the previous evening while nothing was
/// flowing), is written by a batching pump that can lag the stream, and
/// <c>live_bars</c> is a minute behind by construction. It is also where
/// Sentinel's feed-silent rule reads (sentinel/agents/health.py
/// <c>_read_stream</c>), so the two never disagree about whether ticks are
/// arriving. Read the same way: newest entry first, a tick's time is its
/// receivedUtc (the entry id's time when it has none), replays skipped, a
/// bounded number of entries.
/// </remarks>
public sealed class RedisFeedTickSource : IFeedTickSource
{
    public const string Stream = "market:ticks";

    /// <summary>A healthy stream answers in the first few entries; the rest are only read when a group is missing.</summary>
    public const int FirstPage = 250;
    public const int PageSize = 2000;
    public const int MaxPages = 5;

    private const ulong MaxSequence = ulong.MaxValue;

    private readonly IConnectionMultiplexer _redis;

    public RedisFeedTickSource(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    /// <summary>
    /// The group an exchange belongs to: NSE and its derivatives, BSE and its
    /// derivatives, MCX. Sentinel folds NSE and BSE into one "NSE/BSE" group;
    /// they are kept apart here so a SENSEX-only silence can be told from a
    /// dead feed in the message.
    /// </summary>
    public static string? GroupOf(string? exchange) => (exchange ?? string.Empty).Trim().ToUpperInvariant() switch
    {
        "NSE" or "NFO" => FeedFailoverService.Nse,
        "BSE" or "BFO" => FeedFailoverService.Bse,
        "MCX" => FeedFailoverService.Mcx,
        _ => null,
    };

    public async Task<FeedTickReading> ReadAsync(DateTime nowUtc, TimeSpan horizon, CancellationToken cancellationToken)
    {
        var db = _redis.GetDatabase();
        var newest = new Dictionary<string, FeedTick>(StringComparer.Ordinal);
        var horizonUtc = nowUtc - horizon;
        int scanned = 0;
        DateTime? oldest = null;
        bool exhausted = false;
        RedisValue upper = "+";

        for (int page = 0; page < MaxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = page == 0 ? FirstPage : PageSize;
            var entries = await db.StreamRangeAsync(Stream, minId: "-", maxId: upper, count: count, messageOrder: Order.Descending);
            if (entries.Length == 0)
            {
                exhausted = true;
                break;
            }

            foreach (var entry in entries)
            {
                scanned++;
                var idTime = IdTime(entry.Id);
                if (idTime is not null) oldest = idTime;
                Consider(newest, entry["exchange"], entry["symbol"], entry["payload"], idTime);
            }

            if (entries.Length < count)
            {
                exhausted = true;
                break;
            }
            if (newest.ContainsKey(FeedFailoverService.Nse) && newest.ContainsKey(FeedFailoverService.Bse)) break;
            if (oldest is { } o && o < horizonUtc) break;

            if (PreviousId(entries[^1].Id.ToString()) is not { } previous)
            {
                exhausted = true;
                break;
            }
            upper = previous;
        }

        return new FeedTickReading(newest, oldest, exhausted, scanned);
    }

    /// <summary>
    /// Folds one stream entry into <paramref name="newest"/>. Entries come
    /// newest first, so a group's first live entry is its newest and later ones
    /// are not parsed at all.
    /// </summary>
    public static void Consider(Dictionary<string, FeedTick> newest, string? exchangeField, string? symbolField, string? payload, DateTime? idTime)
    {
        string exchange = exchangeField ?? string.Empty;
        string symbol = symbolField ?? string.Empty;
        string? group = GroupOf(exchange) ?? GroupOf(PrefixOf(symbol));
        if (group is not null && newest.ContainsKey(group)) return;

        DateTime? at = null;
        string source = string.Empty;
        if (!string.IsNullOrEmpty(payload))
        {
            try
            {
                using var json = JsonDocument.Parse(payload);
                var root = json.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("isReplay", out var replay) && replay.ValueKind == JsonValueKind.True) return;
                    if (root.TryGetProperty("receivedUtc", out var received) && received.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(received.GetString(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                    {
                        at = parsed.UtcDateTime;
                    }
                    if (root.TryGetProperty("sourceKey", out var sourceKey) && sourceKey.ValueKind == JsonValueKind.String)
                    {
                        source = sourceKey.GetString() ?? string.Empty;
                    }
                    if (group is null && root.TryGetProperty("exchange", out var ex) && ex.ValueKind == JsonValueKind.String)
                    {
                        group = GroupOf(ex.GetString());
                    }
                    if (string.IsNullOrEmpty(symbol) && root.TryGetProperty("symbol", out var sym) && sym.ValueKind == JsonValueKind.String)
                    {
                        symbol = sym.GetString() ?? string.Empty;
                    }
                }
            }
            catch (JsonException)
            {
                // A payload that is not JSON still has a time: the entry's.
            }
        }

        if (group is null || newest.ContainsKey(group)) return;
        at ??= idTime;
        if (at is null) return;
        newest[group] = new FeedTick(at.Value, symbol, source);
    }

    private static string PrefixOf(string symbol)
    {
        int colon = symbol.IndexOf(':');
        return colon > 0 ? symbol[..colon] : string.Empty;
    }

    /// <summary>The time in a stream id "1727498400123-0".</summary>
    public static DateTime? IdTime(RedisValue id)
    {
        var text = id.ToString();
        int dash = text.IndexOf('-');
        var ms = dash > 0 ? text[..dash] : text;
        return long.TryParse(ms, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime
            : null;
    }

    /// <summary>The id just before <paramref name="id"/>: an exclusive bound that works on any Redis version (Sentinel's _previous_id).</summary>
    public static string? PreviousId(string id)
    {
        var parts = id.Split('-', 2);
        if (parts.Length != 2
            || !ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
            || !ulong.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
        {
            return null;
        }
        if (seq > 0) return $"{ms}-{seq - 1}";
        if (ms > 0) return $"{ms - 1}-{MaxSequence}";
        return null;
    }
}

/// <summary>
/// The feeds through the same supervisors the console and the morning job use:
/// the Dhan feed from <see cref="FeedSupervisorRegistry"/>, FYERS as the
/// <see cref="IngestorSupervisor"/> and its <see cref="ChainPollerSupervisor"/>.
/// </summary>
public sealed class SupervisedFeedFailoverFeeds : IFeedFailoverFeeds
{
    private readonly FeedSupervisorRegistry _feeds;
    private readonly IngestorSupervisor _fyers;
    private readonly ChainPollerSupervisor _fyersChain;

    public SupervisedFeedFailoverFeeds(FeedSupervisorRegistry feeds, IngestorSupervisor fyers, ChainPollerSupervisor fyersChain)
    {
        _feeds = feeds;
        _fyers = fyers;
        _fyersChain = fyersChain;
    }

    public async Task<DhanFeedProcess> DhanAsync(CancellationToken cancellationToken)
    {
        if (_feeds.Get(DhanProvider.Key) is not { } dhan) return new DhanFeedProcess(false, null, null);
        var status = await dhan.GetStatusAsync(cancellationToken);
        return status.IsRunning
            ? new DhanFeedProcess(true, status.ProcessId, StartedUtc(status.ProcessId))
            : new DhanFeedProcess(false, null, null);
    }

    public async Task<bool> FyersRunningAsync(CancellationToken cancellationToken)
        => (await _fyers.GetStatusAsync(cancellationToken)).IsRunning;

    public async Task<string> StopDhanAsync(string reason, CancellationToken cancellationToken)
    {
        if (_feeds.Get(DhanProvider.Key) is not { } dhan) return "Dhan feed: none registered";
        var outcome = await dhan.StopAsync(reason, cancellationToken);
        return $"Dhan feed: {outcome.Message}" + (outcome.ExitConfirmed ? string.Empty : " (its exit is not confirmed)");
    }

    /// <summary>The FYERS half of market-open.sh's fallback: the feed, then the chain poller.</summary>
    public async Task<FyersStartOutcome> StartFyersAsync(CancellationToken cancellationToken)
    {
        var feed = await _fyers.StartAsync(cancellationToken);
        var chain = await _fyersChain.StartAsync(cancellationToken);
        // 400 is "already running", which is as good as started.
        bool running = feed.Started || feed.StatusCode == StatusCodes.Status400BadRequest;
        return new FyersStartOutcome(running, $"FYERS feed: {feed.Message}; FYERS chain poller: {chain.Message}");
    }

    private static DateTime? StartedUtc(int? pid)
    {
        if (pid is not { } id) return null;
        try
        {
            using var process = Process.GetProcessById(id);
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            // Not readable (another user, a race with its exit): the service
            // falls back to when it first saw the pid.
            return null;
        }
    }
}

/// <summary>FYERS asked for real, and Dhan's own account of itself.</summary>
public sealed class FeedFailoverChecks : IFeedFailoverChecks
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _http;
    private readonly IOptionsMonitor<FyersSettings> _fyersSettings;
    private readonly TimeProvider _time;

    public FeedFailoverChecks(
        IServiceScopeFactory scopes,
        IHttpClientFactory http,
        IOptionsMonitor<FyersSettings> fyersSettings,
        TimeProvider? time = null)
    {
        _scopes = scopes;
        _http = http;
        _fyersSettings = fyersSettings;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// The session /api/auth/session hands the FYERS feed, then FYERS's
    /// /api/v3/profile with it.
    /// </summary>
    /// <remarks>
    /// "Authenticated" in our own table is not proof: on 10 Sep a token issued
    /// the afternoon before read as authenticated, the feed connected with it,
    /// FYERS answered "Token is expired", and the strategies sat deaf. Switching
    /// to a FYERS feed that cannot sign in would trade one silence for another.
    /// </remarks>
    public async Task<FyersSignIn> CheckFyersAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var session = await scope.ServiceProvider.GetRequiredService<IBrokerSessionStore>().GetCurrentAsync(cancellationToken);
        if (session is null || string.IsNullOrWhiteSpace(session.AccessToken))
        {
            return new FyersSignIn(FyersSignInState.SignedOut, "no FYERS sign-in on record");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (!session.IsAuthenticatedAt(now))
        {
            var expiry = session.ExpiresAtUtc is { } e ? $" at {IstTime.ShortStamp(e)} IST" : string.Empty;
            return new FyersSignIn(FyersSignInState.SignedOut, $"the FYERS token expired{expiry}");
        }

        var credentials = await scope.ServiceProvider.GetRequiredService<IBrokerCredentialsProvider>()
            .GetAsync(FyersProvider.Key, cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(credentials.ClientId))
        {
            return new FyersSignIn(FyersSignInState.Unknown, "the FYERS app id is not configured");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CallTimeout);
        try
        {
            var url = $"{_fyersSettings.CurrentValue.DataApiBaseUrl.TrimEnd('/')}/api/v3/profile";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"{credentials.ClientId}:{session.AccessToken}");
            using var response = await _http.CreateClient(FyersProvider.Key).SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return ReadProfile((int)response.StatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new FyersSignIn(FyersSignInState.Unknown, $"FYERS did not answer its profile call ({ex.GetType().Name})");
        }
    }

    /// <summary>FYERS's answer to /api/v3/profile: <c>{"s":"ok",…}</c>, or <c>{"s":"error","code":-8,"message":"…"}</c>.</summary>
    public static FyersSignIn ReadProfile(int httpStatus, string body)
    {
        string? s = null, message = null, code = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("s", out var sv)) s = sv.ToString();
                if (root.TryGetProperty("message", out var mv)) message = mv.ToString();
                if (root.TryGetProperty("code", out var cv)) code = cv.ToString();
            }
        }
        catch (JsonException)
        {
            // An HTML error page: judged by its status below.
        }

        if (httpStatus is >= 200 and < 300 && string.Equals(s, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return new FyersSignIn(FyersSignInState.SignedIn, "signed in (FYERS answered its profile call)");
        }

        if (string.Equals(s, "error", StringComparison.OrdinalIgnoreCase) || httpStatus is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden)
        {
            var why = string.IsNullOrWhiteSpace(message) ? $"HTTP {httpStatus}" : message;
            return new FyersSignIn(FyersSignInState.SignedOut,
                $"FYERS refused the token: {FeedFailoverService.Clean(why, 160)}" + (code is null ? string.Empty : $" (code {code})"));
        }

        return new FyersSignIn(FyersSignInState.Unknown, $"FYERS answered its profile call with HTTP {httpStatus}");
    }

    /// <summary>Dhan's /profile (is the token accepted?) and the Dhan feed's last heartbeat (what did the feed last say?).</summary>
    public async Task<string> DescribeDhanAsync(CancellationToken cancellationToken)
    {
        var parts = new List<string>(2);
        await using var scope = _scopes.CreateAsyncScope();

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(CallTimeout);
            try
            {
                using var profile = await scope.ServiceProvider.GetRequiredService<DhanApiClient>()
                    .GetAsync("/profile", DhanRateClass.Data, timeout.Token);
                var root = profile.RootElement;
                string? Read(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) ? v.ToString() : null;
                parts.Add($"/profile answers (token valid until {Read("tokenValidity") ?? "?"}, data plan {Read("dataPlan") ?? "?"})");
            }
            catch (DhanApiException ex)
            {
                parts.Add($"/profile refused: {FeedFailoverService.Clean(ex.Message, 160)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                parts.Add($"/profile did not answer ({ex.GetType().Name})");
            }
        }

        try
        {
            var names = ProviderUsageRules.HeartbeatSourceNames(DhanProvider.Key, isLegacyIngestor: false);
            var beat = await scope.ServiceProvider.GetRequiredService<TradingDbContext>().LiveIngestorStatuses
                .AsNoTracking()
                .Where(x => names.Contains(x.SourceName))
                .OrderByDescending(x => x.LastHeartbeatUtc)
                .Select(x => new { x.Status, x.LastHeartbeatUtc, x.LastError })
                .FirstOrDefaultAsync(cancellationToken);
            parts.Add(beat is null
                ? "no feed heartbeat on record"
                : $"feed heartbeat {beat.Status} at {IstTime.ToIst(beat.LastHeartbeatUtc):HH:mm:ss} IST"
                  + (string.IsNullOrWhiteSpace(beat.LastError) ? string.Empty : $", last error: {FeedFailoverService.Clean(beat.LastError, 200)}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            parts.Add($"feed heartbeat unreadable ({ex.GetType().Name})");
        }

        return string.Join("; ", parts);
    }
}
