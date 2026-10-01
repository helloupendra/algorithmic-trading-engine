using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.Replay;

/// <summary>
/// The desk's market replay: a past trading day's recorded ticks played again, in the order they
/// arrived, through the same strategy runners the live desk uses (owner, 1 Oct 2026: "jesa system abhi
/// chal raha hai same, mai isko jab marji ho tb replay kar sku").
/// </summary>
/// <remarks>
/// <para>
/// The runs are recap runs of the replayed day, started first (<c>POST /api/Strategy/start</c> with
/// <c>session: recap</c>). The player waits until each is listening, then plays. Their prices come from
/// <see cref="IMarketReplayBook"/>, never <c>live_quotes_latest</c>. Nothing is stored: the day is
/// already recorded.
/// </para>
/// <para>
/// The replay owns its runs. Whenever it ends (played out, stopped, its player gone, or the market
/// about to open), the runs are stopped at the replay's prices first, and only then is the book
/// cleared.
/// </para>
/// <para>
/// One replay at a time. Its state is kept in <c>system_settings</c> (<see cref="SystemSettingKeys.ReplaySession"/>),
/// so an API restart in the middle of one picks it up again: the player keeps running, and its next
/// ticks refill the book.
/// </para>
/// </remarks>
public sealed class MarketReplayService(
    TradingDbContext db,
    IReplayPlayer player,
    IReplayChannel channel,
    IReplayRunStopper stopper,
    IMarketReplayBook book,
    IMarketSessionService sessions,
    RunPnl pnl,
    IRecapFeeds recapFeeds,
    ILogger<MarketReplayService> logger,
    TimeProvider? time = null) : IReplaySessions
{
    public static readonly IReadOnlyList<int> Speeds = [1, 2, 5, 10];

    /// <summary>The index symbols whose recorded minutes tell how complete a day is.</summary>
    public static readonly IReadOnlyDictionary<string, string> CoverageSymbols = new Dictionary<string, string>
    {
        ["NIFTY"] = "NSE:NIFTY50-INDEX",
        ["BANKNIFTY"] = "NSE:NIFTYBANK-INDEX",
        ["SENSEX"] = "BSE:SENSEX-INDEX",
        ["INDIAVIX"] = "NSE:INDIAVIX-INDEX",
    };

    public const string StateStarting = "starting";
    public const string StateWaiting = "waiting";
    public const string StatePlaying = "playing";
    public const string StatePaused = "paused";
    public const string StateFinished = "finished";
    public const string StateStopped = "stopped";
    public const string StateFailed = "failed";

    private static readonly TimeOnly SessionOpen = new(9, 15);
    private static readonly TimeOnly SessionClose = new(15, 30);
    private static readonly TimeOnly LatestStart = new(15, 0);

    /// <summary>On a trading day a replay must be over by then: the market-open job runs at 08:45.</summary>
    public static readonly TimeOnly MarketMorning = new(8, 45);

    /// <summary>Why a replay still playing at <see cref="MarketMorning"/> on a trading day was stopped. A queue plays that day again.</summary>
    public const string MorningStop = "Stopped at 08:45: NSE opens at 09:15.";

    /// <summary>How long a player may be missing at the start before the replay counts as failed.</summary>
    private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The longest value a system setting holds (<c>system_settings."Value"</c>, varchar(2000)). The in-memory
    /// store takes any length; PostgreSQL refuses the write, and a session or queue that cannot be written
    /// down cannot end or move on.
    /// </summary>
    public const int MaxStateLength = 2000;

    /// <summary>The most of a replay's error that is kept with it.</summary>
    public const int MaxErrorLength = 400;

    /// <summary>
    /// The stored state is read back only by this service. Escaped the default way, every quote, plus sign or
    /// non-ASCII character took six characters of the 2,000.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// One change of the replay's state at a time, across every scope of this API: the monitor's look (and a
    /// queue's next day), the admin's start, stop and queue, and the book reopened for the player after a
    /// restart. Each reads the stored session and queue, decides, then writes them. Two at once both saw
    /// nothing playing and both started a day: a queue's first day was started by its POST and by the
    /// monitor's look in the same moment, and the second start failed the first's replay.
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public static bool IsActive(string? state) => state is StateStarting or StateWaiting or StatePlaying or StatePaused;

    private static async Task<T> GatedAsync<T>(Func<Task<T>> change, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await change();
        }
        finally
        {
            Gate.Release();
        }
    }

    private static Task GatedAsync(Func<Task> change, CancellationToken cancellationToken) =>
        GatedAsync(async () =>
        {
            await change();
            return true;
        }, cancellationToken);

    // ---------- reading ----------

    public async Task<ReplayStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var session = await LoadAsync(cancellationToken);
        var queue = await LoadQueueAsync(cancellationToken);
        var why = session is not null && IsActive(session.State)
            ? $"A replay of {Day(session.Date)} is {session.State}."
            : queue is { EndedUtc: null } ? QueuePlaying : WhyNotNow(Now) ?? await WhyNotBesideRecapAsync(cancellationToken);
        return new ReplayStatus(why is null, why, session is null ? null : await ViewAsync(session, cancellationToken), queue is null ? null : QueueView(queue),
            McxLiveNow(Now));
    }

    /// <summary>
    /// Why a replay now shares the desk with live trading, or null: MCX is in session. The live crude runs read the
    /// same Redis stream (<c>market:ticks</c>) and decode every replayed tick to pass over it, and the player's reads
    /// of <c>live_ticks</c> load the box the live feed runs on. A queued day waits for MCX to close; a replay started
    /// by hand may start, and the status warns.
    /// </summary>
    public string? McxLiveNow(DateTime nowUtc)
    {
        var mcx = sessions.GetSessionInfo(nowUtc, "MCX", "COM");
        return mcx.IsMarketOpen
            ? $"MCX trades until {IstTime.ToIst(mcx.SessionCloseUtc):HH:mm}: live MCX runs share the tick stream with a replay."
            : null;
    }

    private const string QueuePlaying = "A queue of days is playing; cancel it first.";

    /// <summary>
    /// Why a desk replay cannot start beside a vendor's recap feed, or null when none runs. That feed's recap
    /// runs ask for their quotes with <c>replay=true</c>; with a desk replay on, a runner from before the replay
    /// day was sent is answered from the desk replay's book, another day's prices.
    /// </summary>
    private async Task<string?> WhyNotBesideRecapAsync(CancellationToken cancellationToken) =>
        await recapFeeds.RunningAsync(cancellationToken) is { } feed
            ? $"A vendor's recap feed ({feed}) is replaying a session. A desk replay waits until it has stopped."
            : null;

    /// <summary>Why a replay cannot start now, or null: on a trading day, NSE's session and the morning before it are the live desk's.</summary>
    public string? WhyNotNow(DateTime nowUtc)
    {
        var info = sessions.GetSessionInfo(nowUtc, "NSE", "FO");
        if (!info.IsTradingDay) return null;
        var ist = TimeOnly.FromDateTime(IstTime.ToIst(nowUtc));
        return ist >= MarketMorning && nowUtc < info.SessionCloseUtc
            ? "NSE trades today until 15:30. A replay plays after the close, or on a holiday or weekend."
            : null;
    }

    /// <summary>
    /// The recorded days, newest first, with how many of each index's 375 session minutes were recorded. A day is
    /// listed when the exchange calendar has a session on it (<see cref="ReplayableDays"/>) and some of its
    /// minutes from 09:15 to 15:30 were recorded: the player plays no other hours.
    /// </summary>
    public async Task<ReplayDays> DaysAsync(CancellationToken cancellationToken)
    {
        var today = IstTime.DateOf(Now);
        var connection = db.Database.GetDbConnection();
        bool opened = connection.State != System.Data.ConnectionState.Open;
        if (opened) await connection.OpenAsync(cancellationToken);
        try
        {
            var sizes = await ChunkBytesByDayAsync(connection, cancellationToken);
            var recorded = ReplayableDays(sizes.Keys).ToList();
            if (recorded.Count == 0) return new ReplayDays([], null, null);

            var minutes = await MinutesByDayAsync(connection, recorded.Min(), today, cancellationToken);
            var days = recorded
                .OrderByDescending(d => d)
                .Select(d => new ReplayDay(
                    d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    d.DayOfWeek.ToString()[..3],
                    CoverageSymbols.Keys.ToDictionary(k => k, k => minutes.GetValueOrDefault((d, CoverageSymbols[k]))),
                    sizes[d]))
                .Where(d => d.Minutes.Values.Any(m => m > 0))
                .ToList();
            return new ReplayDays(days, days.LastOrDefault()?.Date, days.FirstOrDefault()?.Date);
        }
        finally
        {
            if (opened) await connection.CloseAsync();
        }
    }

    /// <summary>
    /// The recorded days that are over and that NSE or BSE held a session on, by the exchange calendar: a weekend
    /// special session (a Sunday budget day, Diwali's Muhurat) is one, a weekday holiday is not. Until 1 Oct the
    /// weekday decided, and a recorded weekend session was never listed. With no calendar loaded, the weekdays
    /// are the session days, as before.
    /// </summary>
    internal IReadOnlyList<DateOnly> ReplayableDays(IEnumerable<DateOnly> recorded)
    {
        var today = IstTime.DateOf(Now);
        return recorded.Where(d => d < today && (SessionOn(d, "NSE") || SessionOn(d, "BSE"))).OrderBy(d => d).ToList();

        bool SessionOn(DateOnly day, string exchange) => sessions.GetSessionInfo(IstTime.MiddayUtc(day), exchange, "FO").IsTradingDay;
    }

    // ---------- control ----------

    /// <summary>Starts a replay of the request's day for its runs; answers the session, or why not.</summary>
    /// <remarks>
    /// Refused while a queue of days plays, between its days too (the queue starts each of them), and while a
    /// vendor's recap feed runs. The queue checks the recap feed itself, and waits rather than skip a day.
    /// </remarks>
    public async Task<ReplayStartResult> StartAsync(ReplayStartRequest request, string by, CancellationToken cancellationToken)
    {
        var result = await GatedAsync(async () =>
            await LoadQueueAsync(cancellationToken) is { EndedUtc: null }
                ? ReplayStartResult<ReplaySessionState>.Refused(409, QueuePlaying)
                : await WhyNotBesideRecapAsync(cancellationToken) is { } recap
                    ? ReplayStartResult<ReplaySessionState>.Refused(409, recap)
                    : await StartCoreAsync(request, by, cancellationToken), cancellationToken);
        return result.Session is { } started
            ? new ReplayStartResult(await ViewAsync(started, cancellationToken), result.StatusCode, null)
            : ReplayStartResult.Refused(result.StatusCode, result.Error!);
    }

    /// <summary>Starts a replay, under <see cref="Gate"/>: answers the session as stored, or why not.</summary>
    private async Task<ReplayStartResult<ReplaySessionState>> StartCoreAsync(ReplayStartRequest request, string by, CancellationToken cancellationToken)
    {
        var current = await LoadAsync(cancellationToken);
        if (current is not null && IsActive(current.State))
        {
            return ReplayStartResult<ReplaySessionState>.Refused(409, $"A replay of {Day(current.Date)} is {current.State}; stop it first.");
        }

        if (WhyNotNow(Now) is { } closed) return ReplayStartResult<ReplaySessionState>.Refused(409, closed);

        if (!DateOnly.TryParseExact(request.Date, "yyyy-MM-dd", out var date)) return ReplayStartResult<ReplaySessionState>.Refused(400, "date is yyyy-MM-dd.");
        if (date >= IstTime.DateOf(Now)) return ReplayStartResult<ReplaySessionState>.Refused(400, "Only a day that is over can be replayed.");
        if (!Speeds.Contains(request.Speed)) return ReplayStartResult<ReplaySessionState>.Refused(400, $"speed is one of {string.Join(", ", Speeds)}.");
        var from = SessionOpen;
        if (!string.IsNullOrWhiteSpace(request.From)
            && (!TimeOnly.TryParseExact(request.From, "HH:mm", out from) || from < SessionOpen || from > LatestStart))
        {
            return ReplayStartResult<ReplaySessionState>.Refused(400, "from is HH:mm between 09:15 and 15:00.");
        }

        var runIds = (request.RunIds ?? []).Distinct().ToList();
        if (runIds.Count == 0 && !request.AiTrader) return ReplayStartResult<ReplaySessionState>.Refused(400, "Start at least one recap run of the day first, or ask the AI Trader along.");
        if (runIds.Count > 0 && await WhyNotRunsAsync(runIds, date, cancellationToken) is { } badRuns) return ReplayStartResult<ReplaySessionState>.Refused(409, badRuns);
        if (!await RecordedAsync(date, cancellationToken)) return ReplayStartResult<ReplaySessionState>.Refused(409, $"The desk recorded nothing for {Day(date)}.");

        var session = new ReplaySessionState(
            await NextSessionIdAsync(current, cancellationToken), date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), request.Speed,
            from.ToString("HH:mm", CultureInfo.InvariantCulture), StateStarting, runIds, Now, null, null, Plain(by, MaxByLength), request.AiTrader);

        await channel.ResetAsync(cancellationToken);
        book.Begin(date);
        await SaveAsync(session, cancellationToken);

        var (started, message) = await player.StartAsync(
            [
                "--date", session.Date,
                "--speed", session.Speed.ToString(CultureInfo.InvariantCulture),
                "--from", session.FromIst,
                "--session", session.Id.ToString(CultureInfo.InvariantCulture),
                "--runs", string.Join(',', runIds),
            ],
            cancellationToken);
        if (!started)
        {
            book.End();
            session = session with { State = StateFailed, EndedUtc = Now, Error = Cut($"The player did not start: {message}", MaxErrorLength) };
            await SaveAsync(session, cancellationToken);
            return ReplayStartResult<ReplaySessionState>.Refused(500, session.Error!);
        }

        logger.LogInformation("Market replay {Session} of {Date} started by {By} at {Speed}x from {From}, runs {Runs}",
            session.Id, session.Date, by, session.Speed, session.FromIst, string.Join(',', runIds));
        if (McxLiveNow(Now) is { } mcx) logger.LogWarning("Market replay {Session} started while MCX trades: {Why}", session.Id, mcx);
        return new ReplayStartResult<ReplaySessionState>(session, 202, null);
    }

    /// <summary>
    /// The next replay's id: above every id used before, by the stored session, a queue, or the AI Trader's
    /// decisions and shadow positions, which are kept by it. Taken from the stored session alone, a lost or
    /// unreadable setting began the ids at 1 again, and the new replay shared an old one's AI Trader rows.
    /// </summary>
    private async Task<long> NextSessionIdAsync(ReplaySessionState? current, CancellationToken cancellationToken)
    {
        var queue = await LoadQueueAsync(cancellationToken);
        long queued = queue is null ? 0 : Math.Max(queue.LastSession ?? 0, queue.Sessions.Count == 0 ? 0 : queue.Sessions.Max());
        long decided = await db.AiTraderDecisions.AsNoTracking().MaxAsync(d => d.ReplaySessionId, cancellationToken) ?? 0;
        long held = await db.AiTraderShadowPositions.AsNoTracking().MaxAsync(p => p.ReplaySessionId, cancellationToken) ?? 0;
        return new[] { current?.Id ?? 0, queued, decided, held }.Max() + 1;
    }

    public async Task<ReplaySessionView?> StopAsync(string by, CancellationToken cancellationToken)
    {
        var session = await GatedAsync(async () =>
        {
            var current = await LoadAsync(cancellationToken);
            if (current is null || !IsActive(current.State)) return current;

            await player.StopAsync($"stopped by {by}", cancellationToken);
            return await EndAsync(current, StateStopped, null, $"stopped by {by}", cancellationToken);
        }, cancellationToken);
        return session is null ? null : await ViewAsync(session, cancellationToken);
    }

    /// <summary>
    /// Opens the book again for the player's ticks when the API has lost it (a restart) and the stored replay
    /// is still playing; false when no replay is on, which tells the player to stop.
    /// </summary>
    /// <remarks>
    /// The monitor reopens it too, at its first look. The player posts several times a second, and a batch
    /// refused for want of a book fails it (<c>run_replay.py</c>): the replay must not hang on which of the
    /// two the restarted API serves first.
    /// </remarks>
    public async Task<bool> ReopenBookAsync(CancellationToken cancellationToken)
    {
        if (book.Day is not null) return true;
        return await GatedAsync(async () =>
        {
            if (book.Day is not null) return true;
            var session = await LoadAsync(cancellationToken);
            if (session is null || !IsActive(session.State)) return false;
            book.Begin(DateOnly.ParseExact(session.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture));
            logger.LogInformation("Market replay {Session} of {Date}: the book was opened again for the player's ticks", session.Id, session.Date);
            return true;
        }, cancellationToken);
    }

    public async Task<ReplaySessionView?> SendAsync(string command, CancellationToken cancellationToken)
    {
        var session = await LoadAsync(cancellationToken);
        if (session is null || !IsActive(session.State)) return session is null ? null : await ViewAsync(session, cancellationToken);
        await channel.SendAsync(command, cancellationToken);
        return await ViewAsync(session, cancellationToken);
    }

    public IReadOnlyList<string> Logs(int take) => player.Logs(Math.Clamp(take, 1, 500));

    /// <summary>
    /// The monitor's look, every few seconds: mirror the player's state, and end the replay when it is
    /// played out, stopped, gone, or the market is about to open. Public for tests.
    /// </summary>
    public Task TickAsync(CancellationToken cancellationToken) => GatedAsync(() => TickCoreAsync(cancellationToken), cancellationToken);

    private async Task TickCoreAsync(CancellationToken cancellationToken)
    {
        var session = await LoadAsync(cancellationToken);
        if (session is null || !IsActive(session.State))
        {
            await AdvanceQueueAsync(session, cancellationToken);
            return;
        }

        var date = DateOnly.ParseExact(session.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        // An API restart empties the book; the player's next ticks fill it again.
        if (book.Day != date) book.Begin(date);

        if (WhyNotNow(Now) is not null)
        {
            var notStopped = await StopPlayerAsync(session, "the market opens", cancellationToken);
            await EndAsync(session, StateStopped, notStopped is null ? MorningStop : $"{MorningStop} {notStopped}", "the market opens", cancellationToken);
            return;
        }

        var status = await channel.ReadStatusAsync(cancellationToken);
        var mine = status is not null && status.Session == session.Id ? status : null;
        if (mine?.State is StateFinished or StateStopped or StateFailed)
        {
            await EndAsync(session, mine.State, mine.Error, $"the replay {mine.State}", cancellationToken, mine);
            return;
        }

        if (!await player.IsRunningAsync(cancellationToken) && Now - session.StartedUtc > StartGrace)
        {
            await EndAsync(session, StateFailed, "The replay player stopped before the day was played out.", "the player stopped", cancellationToken);
            return;
        }

        if (mine?.State is StateWaiting or StatePlaying or StatePaused && mine.State != session.State)
        {
            await SaveAsync(session with { State = mine.State }, cancellationToken);
        }
    }

    /// <summary>
    /// Stops the player for the 08:45 rule; null when it stopped, or why not. Its replay ends either way: when the
    /// stop threw (its pid could not be verified, say), the session was never ended, the monitor tried again every
    /// 5 s, and the replay's runs and book stayed on into the session. The player is told to stop over Redis too,
    /// and with the replay ended its next post is refused (409): it exits by itself.
    /// </summary>
    private async Task<string?> StopPlayerAsync(ReplaySessionState session, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await player.StopAsync(reason, cancellationToken);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Market replay {Session}: its player could not be stopped ({Reason}); the replay ends all the same", session.Id, reason);
            try
            {
                // A paused player posts nothing, so it would never be refused.
                await channel.SendAsync("stop", cancellationToken);
            }
            catch (Exception sendEx) when (sendEx is not OperationCanceledException)
            {
                logger.LogWarning(sendEx, "Market replay {Session}: the stop could not be sent to its player either", session.Id);
            }

            return $"Its player could not be stopped ({ex.Message}); it exits at its next post.";
        }
    }

    /// <summary>
    /// Stops the replay's runs at the replay's prices, then clears the book. Where its clock stood and how many
    /// ticks the player sent are kept with the ended session: the player's status, which said so while it
    /// played, is forgotten at the next start.
    /// </summary>
    /// <param name="last">The player's last status for this session, when the caller has just read it.</param>
    private async Task<ReplaySessionState> EndAsync(ReplaySessionState session, string state, string? error, string reason, CancellationToken cancellationToken,
        ReplayPlayerStatus? last = null)
    {
        last ??= await LastStatusAsync(session, cancellationToken);
        foreach (var runId in session.RunIds)
        {
            try
            {
                await stopper.StopAsync(runId, $"Market replay of {Day(session.Date)}: {reason}", cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Market replay {Session}: stopping run {RunId} failed", session.Id, runId);
            }
        }

        // The book's clock is the newest tick the API took; the player's, its own once a second. The later wins.
        var date = DateOnly.ParseExact(session.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var clock = new[] { book.Day == date ? book.ClockUtc : null, last?.ClockUtc, session.ClockUtc }.Max();
        book.End();
        var ended = session with
        {
            State = state, EndedUtc = Now, Error = error is null ? null : Cut(error, MaxErrorLength),
            ClockUtc = clock, TicksSent = last?.TicksSent ?? session.TicksSent,
        };
        await SaveAsync(ended, cancellationToken);
        logger.LogInformation("Market replay {Session} of {Date} ended: {State} ({Reason})", session.Id, session.Date, state, reason);
        return ended;
    }

    /// <summary>The player's status for <paramref name="session"/>, or null: Redis failing here must not keep a replay from ending.</summary>
    private async Task<ReplayPlayerStatus?> LastStatusAsync(ReplaySessionState session, CancellationToken cancellationToken)
    {
        try
        {
            var status = await channel.ReadStatusAsync(cancellationToken);
            return status is not null && status.Session == session.Id ? status : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Market replay {Session}: the player's last status could not be read", session.Id);
            return null;
        }
    }

    // ---------- the queue ----------

    /// <summary>
    /// The most days one queue may hold. With each skipped day's reason and the account's name kept short and
    /// plain (<see cref="Plain"/>), its state always fits <see cref="MaxStateLength"/>.
    /// </summary>
    public const int MaxQueuedDays = 20;

    /// <summary>How much of a skipped day's reason is kept.</summary>
    private const int MaxReasonLength = 40;

    /// <summary>How much of an account's name is kept with a session or a queue.</summary>
    private const int MaxByLength = 60;

    /// <summary>
    /// How long after a replay ends the next queued day waits: the AI Trader's minute check squares off and
    /// closes what that replay's shadow book still held.
    /// </summary>
    public static readonly TimeSpan QueueGap = TimeSpan.FromSeconds(90);

    /// <summary>
    /// The tries a queued day gets in all when it does not play for a reason that passes: its player did not start,
    /// or its replay failed soon after it started (<see cref="EarlyFailure"/>), as when the database, Redis or the API
    /// was down a while; or the 08:45 rule stopped it. Until 1 Oct each such day was written down as played or
    /// skipped at once, and every day the queue reached during an outage was lost.
    /// </summary>
    public const int MaxTries = 3;

    /// <summary>How long a queued day waits before its second try, and before its third.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryWaits = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    /// <summary>
    /// A queued day whose replay failed this soon after it started, or failed having played nothing, did not play,
    /// and is tried again. One that failed later on counts as played: its AI Trader day is there, short.
    /// </summary>
    public static readonly TimeSpan EarlyFailure = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Queues recorded days to be replayed one after another, each from its open, with the AI Trader deciding
    /// along and no strategy runs: its shadow book over many days, to be scored against a simple rule. The first
    /// starts now if it can; the monitor starts each next one.
    /// </summary>
    public async Task<ReplayStartResult<ReplayQueueView>> QueueAsync(ReplayQueueRequest request, string by, CancellationToken cancellationToken)
    {
        var result = await GatedAsync(async () =>
        {
            var current = await LoadQueueAsync(cancellationToken);
            if (current is { EndedUtc: null }) return ReplayStartResult<ReplayQueueState>.Refused(409, "A queue of days is already playing; cancel it first.");
            var session = await LoadAsync(cancellationToken);
            if (session is not null && IsActive(session.State)) return ReplayStartResult<ReplayQueueState>.Refused(409, $"A replay of {Day(session.Date)} is {session.State}; stop it first.");
            if (!Speeds.Contains(request.Speed)) return ReplayStartResult<ReplayQueueState>.Refused(400, $"speed is one of {string.Join(", ", Speeds)}.");

            var dates = new SortedSet<DateOnly>();
            foreach (var text in request.Dates ?? [])
            {
                if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", out var d)) return ReplayStartResult<ReplayQueueState>.Refused(400, $"\"{text}\" is not a yyyy-MM-dd date.");
                if (d >= IstTime.DateOf(Now)) return ReplayStartResult<ReplayQueueState>.Refused(400, $"{Day(d)} is not over yet.");
                dates.Add(d);
            }

            if (dates.Count == 0) return ReplayStartResult<ReplayQueueState>.Refused(400, "Send at least one recorded day.");
            if (dates.Count > MaxQueuedDays) return ReplayStartResult<ReplayQueueState>.Refused(400, $"At most {MaxQueuedDays} days a queue.");

            var queue = new ReplayQueueState((current?.Id ?? 0) + 1,
                dates.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList(), request.Speed,
                SessionOpen.ToString("HH:mm", CultureInfo.InvariantCulture), 0, [], [], Plain(by, MaxByLength), Now, null, null);
            await SaveQueueAsync(queue, cancellationToken);
            logger.LogInformation("Replay queue {Queue}: {Count} days at {Speed}x queued by {By}", queue.Id, queue.Dates.Count, queue.Speed, by);
            await AdvanceQueueAsync(session, cancellationToken);
            return new ReplayStartResult<ReplayQueueState>(await LoadQueueAsync(cancellationToken), 202, null);
        }, cancellationToken);
        return result.Session is { } queued
            ? new ReplayStartResult<ReplayQueueView>(QueueView(queued), result.StatusCode, null)
            : ReplayStartResult<ReplayQueueView>.Refused(result.StatusCode, result.Error!);
    }

    /// <summary>Ends the queue: no further day starts. The day playing now plays on unless it is stopped.</summary>
    public async Task<ReplayQueueView?> CancelQueueAsync(string by, CancellationToken cancellationToken)
    {
        var queue = await GatedAsync(async () =>
        {
            var stored = await LoadQueueAsync(cancellationToken);
            if (stored is null || stored.EndedUtc is not null) return stored;
            var cancelled = stored with { EndedUtc = Now, Note = $"Cancelled by {Plain(by, MaxByLength)}." };
            await SaveQueueAsync(cancelled, cancellationToken);
            logger.LogInformation("Replay queue {Queue} cancelled by {By}", cancelled.Id, by);
            return cancelled;
        }, cancellationToken);
        return queue is null ? null : QueueView(queue);
    }

    /// <summary>
    /// Starts the queue's next day when nothing is playing, the last replay ended at least <see cref="QueueGap"/>
    /// ago, its player has exited, a retry's wait is over, no exchange the desk trades is in session (NSE with its
    /// 08:45 morning, MCX), and the day can be played out before either next opens (<see cref="FitsBeforeLiveTrading"/>).
    /// A day that did not play for a reason that passes (<see cref="NotPlayed"/>, a player that
    /// did not start) is tried again, at most <see cref="MaxTries"/> times; one that cannot play (nothing
    /// recorded) is skipped with its reason. Under <see cref="Gate"/>.
    /// </summary>
    private async Task AdvanceQueueAsync(ReplaySessionState? session, CancellationToken cancellationToken)
    {
        var queue = await LoadQueueAsync(cancellationToken);
        if (queue is null || queue.EndedUtc is not null) return;

        // A day this queue started but never wrote down: the API stopped, or a write failed, between the
        // player's start and the queue's. It is that day's session, not a day still to play.
        if (session is not null && StartedForNext(queue, session))
        {
            queue = Counted(queue, session.Id);
            await SaveQueueAsync(queue, cancellationToken);
            logger.LogWarning("Replay queue {Queue}: session {Session} of {Date} was started but not recorded; recorded now", queue.Id, session.Id, session.Date);
        }

        if (session is not null && IsActive(session.State)) return;

        // The day just played ended without playing: it is the next day again, or after its last try skipped.
        if (session is not null && NotPlayed(queue, session) is { } why)
        {
            queue = TryAgain(queue, queue.Next - 1, session.Id, why, wait: why != MorningStop);
            await SaveQueueAsync(queue, cancellationToken);
        }

        if (session?.EndedUtc is DateTime ended && Now - ended < QueueGap) return;

        if (queue.Next >= queue.Dates.Count)
        {
            await SaveQueueAsync(queue with { EndedUtc = Now, Note = queue.Note ?? "Every day was played." }, cancellationToken);
            logger.LogInformation("Replay queue {Queue} done: {Count} days", queue.Id, queue.Dates.Count);
            return;
        }

        // No queued day plays beside live trading: not in NSE's session or the morning before it, not while MCX
        // trades (the live crude runs share the tick stream and the box), and not into either's next open.
        if (WhyNotNow(Now) is not null || McxLiveNow(Now) is not null || !FitsBeforeLiveTrading(queue.Speed, queue.FromIst)) return;

        // A day being tried again waits a while first: what failed it may not have come back yet.
        if (queue.RetryUtc is DateTime retry && Now < retry) return;

        // A vendor's recap feed is replaying a session: the day waits for it to end, it is not skipped.
        if (await recapFeeds.RunningAsync(cancellationToken) is not null) return;

        // The last day's player has not exited yet, or its pid cannot be verified just now: the supervisor would
        // refuse a second one, and the day would be skipped for a wait of seconds.
        if (await player.IsRunningAsync(cancellationToken)) return;

        string date = queue.Dates[queue.Next];
        var result = await StartCoreAsync(new ReplayStartRequest(date, queue.Speed, queue.FromIst, [], AiTrader: true), queue.By, cancellationToken);
        if (result.Session is { } started)
        {
            queue = Counted(queue, started.Id);
        }
        else if (result.StatusCode >= 500)
        {
            // The player did not start: the engine, the database or Redis may be down a while. The failed
            // session the start wrote down is the try's.
            queue = TryAgain(queue, queue.Next, (await LoadAsync(cancellationToken))?.Id, result.Error ?? "The player did not start.", wait: true);
        }
        else
        {
            queue = queue with { Next = queue.Next + 1, Skipped = [.. queue.Skipped, $"{date}: {Plain(result.Error, MaxReasonLength)}"] };
        }

        await SaveQueueAsync(queue, cancellationToken);
    }

    /// <summary>The queue with the day at <c>Next</c> started as <paramref name="sessionId"/>.</summary>
    private static ReplayQueueState Counted(ReplayQueueState queue, long sessionId) => queue with
    {
        Next = queue.Next + 1, Sessions = [.. queue.Sessions, sessionId], RetryUtc = null, LastSession = Later(queue.LastSession, sessionId),
    };

    /// <summary>
    /// Why the day the queue played last, as <paramref name="session"/>, did not play after all; null when it
    /// counts as played. It did not play when its replay failed within <see cref="EarlyFailure"/> of its start or
    /// with nothing played (Redis down: the failure may be seen late), or when the 08:45 rule stopped it. It counts
    /// as played when it played out, failed later on, or was stopped by hand: the owner stopped it.
    /// </summary>
    private static string? NotPlayed(ReplayQueueState queue, ReplaySessionState session)
    {
        if (session.EndedUtc is not DateTime ended || queue.Sessions.Count == 0 || queue.Sessions[^1] != session.Id
            || queue.Next == 0 || queue.Dates[queue.Next - 1] != session.Date)
        {
            return null;
        }

        if (session.State == StateStopped && session.Error?.StartsWith(MorningStop, StringComparison.Ordinal) == true) return MorningStop;
        bool early = ended - session.StartedUtc <= EarlyFailure || (session.ClockUtc is null && session.TicksSent == 0);
        return session.State == StateFailed && early ? session.Error ?? "The replay failed." : null;
    }

    /// <summary>
    /// One more try of the day at <paramref name="index"/>, which did not play (<paramref name="why"/>): it is
    /// the next day again, after <see cref="RetryWaits"/> (with no wait after the 08:45 rule: the next allowed
    /// time is wait enough), or, after its last try, skipped with why. The try's session, <paramref name="sessionId"/>,
    /// is not one of the days played.
    /// </summary>
    private ReplayQueueState TryAgain(ReplayQueueState queue, int index, long? sessionId, string why, bool wait)
    {
        var tries = TriesOf(queue);
        tries[index]++;
        var sessions = queue.Sessions.Where(s => s != sessionId).ToList();
        var last = Later(queue.LastSession, sessionId);
        string date = queue.Dates[index];
        string written = string.Concat(tries);

        if (tries[index] >= MaxTries)
        {
            logger.LogWarning("Replay queue {Queue}: {Date} did not play in {Tries} tries ({Why}); skipped", queue.Id, date, tries[index], why);
            return queue with
            {
                Next = index + 1, Sessions = sessions, Skipped = [.. queue.Skipped, $"{date}: {Plain($"{tries[index]} tries; {why}", MaxReasonLength)}"],
                Tries = written, RetryUtc = null, LastSession = last,
            };
        }

        DateTime? retry = wait ? Now + RetryWaits[Math.Min(tries[index], RetryWaits.Count) - 1] : null;
        logger.LogWarning("Replay queue {Queue}: {Date} did not play ({Why}); try {Try} of {Max} {When}", queue.Id, date, why, tries[index] + 1, MaxTries,
            retry is DateTime at ? $"from {IstTime.ShortStamp(at)} IST" : "at the next allowed time");
        return queue with { Next = index, Sessions = sessions, Tries = written, RetryUtc = retry, LastSession = last };
    }

    /// <summary>
    /// Each day's tries that did not play, beside <c>Dates</c>; none for a queue stored without them. Kept as one
    /// digit a day ("0010"): twenty days take 22 characters of the setting's 2,000.
    /// </summary>
    private static int[] TriesOf(ReplayQueueState queue) =>
        queue.Dates.Select((_, i) => queue.Tries is { } t && i < t.Length && char.IsAsciiDigit(t[i]) ? t[i] - '0' : 0).ToArray();

    private static long? Later(long? a, long? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    /// <summary>
    /// Whether <paramref name="session"/> is the queue's next day, started by the queue and not counted yet. A
    /// session the queue has already seen (counted, or a failed try) is not: <c>LastSession</c> is the latest.
    /// </summary>
    private static bool StartedForNext(ReplayQueueState queue, ReplaySessionState session) =>
        queue.Next < queue.Dates.Count
        && session.Date == queue.Dates[queue.Next]
        && session.Id > (queue.LastSession ?? 0)
        && !queue.Sessions.Contains(session.Id)
        && session.AiTrader && session.RunIds.Count == 0
        && session.StartedUtc >= queue.CreatedUtc;

    /// <summary>
    /// Whether a day played from <paramref name="fromIst"/> at <paramref name="speed"/> would end before live trading
    /// next begins: MCX's next open (from the session service: its holidays, half days and a close that follows New
    /// York's clocks), and the next NSE trading morning's 08:45. On a day MCX trades and NSE does not, MCX's comes first.
    /// </summary>
    private bool FitsBeforeLiveTrading(int speed, string fromIst)
    {
        var from = TimeOnly.ParseExact(fromIst, "HH:mm", CultureInfo.InvariantCulture);
        // The player plays until 15:40; ten minutes more for its start and the last checks.
        var length = TimeSpan.FromMinutes((new TimeOnly(15, 40) - from).TotalMinutes / Math.Max(1, speed) + 10);
        if (Now + length >= sessions.GetNextMarketOpenUtc(Now, "MCX", "COM")) return false;

        var today = IstTime.DateOf(Now);
        for (int i = 0; i <= 7; i++)
        {
            var day = today.AddDays(i);
            var morning = IstTime.FromIst(day.ToDateTime(MarketMorning));
            if (morning <= Now || !sessions.GetSessionInfo(IstTime.FromIst(day.ToDateTime(new TimeOnly(12, 0))), "NSE", "FO").IsTradingDay) continue;
            return Now + length < morning;
        }

        return true;
    }

    private static string Cut(string? text, int max)
    {
        var t = (text ?? string.Empty).Trim();
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }

    /// <summary>
    /// <paramref name="text"/> on one line and without the characters JSON writes as two (quotes, backslashes),
    /// cut to <paramref name="max"/>: so that as many as a queue holds always fit its stored state.
    /// </summary>
    private static string Plain(string? text, int max) =>
        Cut(new string((text ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c).Where(c => c is not ('"' or '\\')).ToArray()), max);

    private static ReplayQueueView QueueView(ReplayQueueState q) => new(
        q.Id, q.Dates, q.Speed, q.FromIst, q.Next, q.Sessions, q.Skipped, q.By, q.CreatedUtc, q.EndedUtc, q.Note,
        q.EndedUtc is null ? (q.Next < q.Dates.Count ? q.Dates[q.Next] : null) : null, TriesOf(q), q.EndedUtc is null ? q.RetryUtc : null);

    public async Task<ReplayQueueState?> LoadQueueAsync(CancellationToken cancellationToken)
    {
        string? json = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SystemSettingKeys.ReplayQueue).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<ReplayQueueState>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task SaveQueueAsync(ReplayQueueState queue, CancellationToken cancellationToken)
    {
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == SystemSettingKeys.ReplayQueue, cancellationToken);
        if (row is null)
        {
            row = new SystemSetting { Key = SystemSettingKeys.ReplayQueue, CreatedUtc = Now };
            db.SystemSettings.Add(row);
        }

        row.Value = JsonSerializer.Serialize(queue, Json);
        row.UpdatedBy = Cut(queue.By, 100);
        row.UpdatedUtc = Now;
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---------- the session ----------

    private async Task<ReplaySessionView> ViewAsync(ReplaySessionState session, CancellationToken cancellationToken)
    {
        var status = IsActive(session.State) ? await channel.ReadStatusAsync(cancellationToken) : null;
        var mine = status is not null && status.Session == session.Id ? status : null;
        var date = DateOnly.ParseExact(session.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        // Ended, the player's status is gone (or another replay's): the session keeps where its clock stood.
        DateTime? clock = mine?.ClockUtc ?? (book.Day == date ? book.ClockUtc : null) ?? session.ClockUtc;

        var runs = await db.SimulationRuns.AsNoTracking()
            .Where(r => session.RunIds.Contains(r.Id))
            .Select(r => new { r.Id, r.StrategyName, r.Symbol, r.ParametersJson, r.Status, r.UserId })
            .ToListAsync(cancellationToken);
        var userIds = runs.Select(r => r.UserId).Distinct().ToList();
        var users = await db.AppUsers.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.UserName, cancellationToken);
        var running = runs.Where(r => r.Status == StrategyRunControl.RunStatusRunning).Select(r => r.Id).ToHashSet();
        var figures = await pnl.FiguresAsync(session.RunIds, running, cancellationToken);

        var views = session.RunIds
            .Select(id => runs.FirstOrDefault(r => r.Id == id) is { } r
                ? new ReplayRunView(r.Id, r.StrategyName, UnderlyingOf(r.ParametersJson, r.Symbol), users.GetValueOrDefault(r.UserId), r.Status,
                    figures.TryGetValue(r.Id, out var f) ? f.Realized + f.Unrealized - f.Charges : 0m)
                : new ReplayRunView(id, "?", null, null, "Missing", 0m))
            .ToList();

        return new ReplaySessionView(
            session.Id, session.Date, session.Speed, session.FromIst, mine?.State is { } live && IsActive(session.State) ? live : session.State,
            clock, clock is DateTime c ? IstTime.ToIst(c).ToString("HH:mm:ss", CultureInfo.InvariantCulture) : null,
            mine?.Progress ?? ProgressOf(clock, date, session.State), mine?.TicksSent ?? session.TicksSent, session.StartedUtc, session.EndedUtc,
            session.Error ?? mine?.Error, session.RunIds, views, session.StartedBy, session.AiTrader);
    }

    /// <summary>How far through the replayed session (09:15–15:30) a clock is, 0 to 1.</summary>
    public static double ProgressOf(DateTime? clockUtc, DateOnly day, string state)
    {
        if (state == StateFinished) return 1;
        if (clockUtc is not DateTime clock) return 0;
        var open = IstTime.FromIst(day.ToDateTime(SessionOpen));
        var close = IstTime.FromIst(day.ToDateTime(SessionClose));
        return Math.Clamp((clock - open).TotalSeconds / (close - open).TotalSeconds, 0, 1);
    }

    private async Task<string?> WhyNotRunsAsync(IReadOnlyList<long> runIds, DateOnly date, CancellationToken cancellationToken)
    {
        var runs = await db.SimulationRuns.AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Status, r.Mode, r.ParametersJson })
            .ToListAsync(cancellationToken);
        var problems = new List<string>();
        foreach (var id in runIds)
        {
            var run = runs.FirstOrDefault(r => r.Id == id);
            if (run is null) problems.Add($"run {id} does not exist");
            else if (run.Status != StrategyRunControl.RunStatusRunning) problems.Add($"run {id} is {run.Status.ToLowerInvariant()}");
            else if (!RecapClock.IsRecap(run.ParametersJson)) problems.Add($"run {id} is not a recap run");
            else if (RecapClock.RecapDate(run.ParametersJson) != date) problems.Add($"run {id} is a recap of another day");
        }

        return problems.Count == 0 ? null : "Every run must be a running recap run of the day: " + string.Join("; ", problems) + ".";
    }

    private async Task<bool> RecordedAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var from = IstTime.FromIst(date.ToDateTime(SessionOpen));
        var to = IstTime.FromIst(date.ToDateTime(SessionClose));
        var symbols = CoverageSymbols.Values.ToList();
        return await db.LiveBars.AsNoTracking()
            .AnyAsync(b => symbols.Contains(b.Symbol) && b.Resolution == "1m" && b.BarStartUtc >= from && b.BarStartUtc < to, cancellationToken);
    }

    public async Task<ReplaySessionState?> LoadAsync(CancellationToken cancellationToken)
    {
        string? json = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SystemSettingKeys.ReplaySession).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<ReplaySessionState>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task SaveAsync(ReplaySessionState session, CancellationToken cancellationToken)
    {
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == SystemSettingKeys.ReplaySession, cancellationToken);
        if (row is null)
        {
            row = new SystemSetting { Key = SystemSettingKeys.ReplaySession, CreatedUtc = Now };
            db.SystemSettings.Add(row);
        }

        row.Value = JsonSerializer.Serialize(session, Json);
        row.UpdatedBy = Cut(session.StartedBy, 100);
        row.UpdatedUtc = Now;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string? UnderlyingOf(string? parametersJson, string? symbol)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(parametersJson)
                && JsonDocument.Parse(parametersJson).RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("underlying", out var u) && u.ValueKind == JsonValueKind.String)
            {
                return u.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return symbol;
    }

    private static string Day(string date) =>
        DateOnly.TryParseExact(date, "yyyy-MM-dd", out var d) ? Day(d) : date;

    private static string Day(DateOnly date) => date.ToString("d MMM", CultureInfo.InvariantCulture);

    // ---------- coverage ----------

    private static async Task<Dictionary<DateOnly, long>> ChunkBytesByDayAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        // The catalog only: each daily chunk of live_ticks and its size on disk.
        var result = new Dictionary<DateOnly, long>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select c.range_start, s.total_bytes
            from timescaledb_information.chunks c
            join chunks_detailed_size('public.live_ticks'::regclass) s
              on s.chunk_schema = c.chunk_schema and s.chunk_name = c.chunk_name
            where c.hypertable_schema = 'public' and c.hypertable_name = 'live_ticks'
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var start = reader.GetFieldValue<DateTime>(0);
            var day = IstTime.DateOf(DateTime.SpecifyKind(start.ToUniversalTime().AddHours(6), DateTimeKind.Utc));
            result[day] = result.GetValueOrDefault(day) + (reader.IsDBNull(1) ? 0 : reader.GetInt64(1));
        }

        return result;
    }

    /// <summary>
    /// Each coverage symbol's recorded 1m bars from 09:15 to 15:30 IST, by IST day. <c>"BarStartUtc"</c> is a
    /// timestamptz: <c>at time zone 'UTC'</c> makes it a plain timestamp of the UTC wall clock, which moves to IST
    /// and casts to a date and a time the same whatever the connection's TimeZone. Cast straight from the
    /// timestamptz (<c>("BarStartUtc" + interval)::date</c>) it was read in that TimeZone: right only on a UTC
    /// server.
    /// </summary>
    internal const string MinutesByDaySql = """
        select ((b."BarStartUtc" at time zone 'UTC') + interval '5 hours 30 minutes')::date as day, b."Symbol", count(distinct b."BarStartUtc")
        from live_bars b
        where b."Resolution" = '1m' and b."Symbol" = any(@symbols)
          and b."BarStartUtc" >= @from and b."BarStartUtc" < @to
          and ((b."BarStartUtc" at time zone 'UTC') + interval '5 hours 30 minutes')::time >= '09:15'
          and ((b."BarStartUtc" at time zone 'UTC') + interval '5 hours 30 minutes')::time < '15:30'
        group by 1, 2
        """;

    private static async Task<Dictionary<(DateOnly, string), int>> MinutesByDayAsync(DbConnection connection, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var result = new Dictionary<(DateOnly, string), int>();
        await using var command = connection.CreateCommand();
        command.CommandText = MinutesByDaySql;
        Add(command, "symbols", CoverageSymbols.Values.ToArray());
        Add(command, "from", IstTime.FromIst(from.ToDateTime(TimeOnly.MinValue)));
        Add(command, "to", IstTime.FromIst(to.ToDateTime(TimeOnly.MinValue)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var day = DateOnly.FromDateTime(reader.GetFieldValue<DateTime>(0));
            result[(day, reader.GetString(1))] = (int)reader.GetInt64(2);
        }

        return result;

        static void Add(DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }
}

/// <summary>The replay in progress or last played, as other parts of the desk read it.</summary>
public interface IReplaySessions
{
    Task<ReplaySessionState?> LoadAsync(CancellationToken cancellationToken);
}

// ---------- what the service talks to (interfaces, so tests need no process, Redis or runner) ----------

/// <summary>The replay's player process.</summary>
public interface IReplayPlayer
{
    Task<bool> IsRunningAsync(CancellationToken cancellationToken);

    Task<(bool Started, string Message)> StartAsync(IReadOnlyList<string> args, CancellationToken cancellationToken);

    Task StopAsync(string reason, CancellationToken cancellationToken);

    IReadOnlyList<string> Logs(int take);
}

/// <summary>What the player says (Redis <c>replay:status</c>) and what it is told (<c>replay:control</c>).</summary>
public interface IReplayChannel
{
    Task<ReplayPlayerStatus?> ReadStatusAsync(CancellationToken cancellationToken);

    /// <summary>"pause" or "resume"; "stop" when the player's process could not be stopped.</summary>
    Task SendAsync(string command, CancellationToken cancellationToken);

    /// <summary>Forgets the last replay's status and any command left for it.</summary>
    Task ResetAsync(CancellationToken cancellationToken);
}

/// <summary>Stops one of the replay's runs, squaring it off.</summary>
public interface IReplayRunStopper
{
    Task StopAsync(long runId, string reason, CancellationToken cancellationToken);
}

/// <summary>
/// A vendor's recap feed running now (TrueData replaying a session from its own host in the evening), by the
/// name it heartbeats under; null when none is. Its ticks go where a live feed's go, its recap runs read their
/// quotes with <c>replay=true</c>, and a desk replay on at the same time would answer them from its own book.
/// </summary>
public interface IRecapFeeds
{
    Task<string?> RunningAsync(CancellationToken cancellationToken);
}

// ---------- the wire ----------

public sealed record ReplayPlayerStatus(long Session, string State, DateTime? ClockUtc, long TicksSent, double? Progress, string? Error, DateTime? UpdatedUtc);

/// <param name="AiTrader">The AI Trader decides along, on the replay's clock (it must be switched on; it places nothing in a replay).</param>
public sealed record ReplayStartRequest(string? Date, int Speed, string? From, IReadOnlyList<long>? RunIds, bool AiTrader = false);

/// <param name="ClockUtc">Where the replay's clock stood when it ended; null while it plays (the player's status says).</param>
/// <param name="TicksSent">How many ticks the player had sent when it ended.</param>
public sealed record ReplaySessionState(
    long Id, string Date, int Speed, string FromIst, string State, IReadOnlyList<long> RunIds,
    DateTime StartedUtc, DateTime? EndedUtc, string? Error, string StartedBy, bool AiTrader = false,
    DateTime? ClockUtc = null, long TicksSent = 0);

public sealed record ReplayRunView(long RunId, string Strategy, string? Underlying, string? Account, string Status, decimal NetPnl);

public sealed record ReplaySessionView(
    long Id, string Date, int Speed, string FromIst, string State, DateTime? ClockUtc, string? ClockIst, double Progress,
    long TicksSent, DateTime StartedUtc, DateTime? EndedUtc, string? Error, IReadOnlyList<long> RunIds,
    IReadOnlyList<ReplayRunView> Runs, string StartedBy, bool AiTrader = false);

/// <param name="Warning">Why a replay started now would share the desk with live trading, though it may start: MCX in session.</param>
public sealed record ReplayStatus(bool CanStart, string? WhyNot, ReplaySessionView? Session, ReplayQueueView? Queue = null, string? Warning = null);

/// <summary>Days to replay one after another with the AI Trader (yyyy-MM-dd), and the speed.</summary>
public sealed record ReplayQueueRequest(IReadOnlyList<string>? Dates, int Speed = 2);

/// <summary>
/// A queue of days: the next to play is <c>Dates[Next]</c>; each played day's replay session is in <c>Sessions</c>
/// and each skipped day is in <c>Skipped</c>, with why. Kept in one setting of at most 2,000 characters.
/// </summary>
/// <param name="Tries">Beside <c>Dates</c>, one digit a day: its tries that did not play (failed early, the player did
/// not start, stopped at 08:45), as "0010". Null in a queue stored before 1 Oct, or with none yet.</param>
/// <param name="RetryUtc">When the day being tried again may start; null when it need not wait.</param>
/// <param name="LastSession">The latest replay session the queue has seen (a day played, or a failed try): any later
/// one of its next day was started by it and not written down.</param>
public sealed record ReplayQueueState(
    long Id, IReadOnlyList<string> Dates, int Speed, string FromIst, int Next, IReadOnlyList<long> Sessions, IReadOnlyList<string> Skipped,
    string By, DateTime CreatedUtc, DateTime? EndedUtc, string? Note, string? Tries = null, DateTime? RetryUtc = null, long? LastSession = null);

public sealed record ReplayQueueView(
    long Id, IReadOnlyList<string> Dates, int Speed, string FromIst, int Next, IReadOnlyList<long> Sessions, IReadOnlyList<string> Skipped,
    string By, DateTime CreatedUtc, DateTime? EndedUtc, string? Note, string? NextDate, IReadOnlyList<int>? Tries = null, DateTime? RetryUtc = null);

public sealed record ReplayDay(string Date, string Weekday, IReadOnlyDictionary<string, int> Minutes, long SizeBytes);

public sealed record ReplayDays(IReadOnlyList<ReplayDay> Days, string? Earliest, string? Latest);

public sealed record ReplayStartResult<T>(T? Session, int StatusCode, string? Error) where T : class
{
    public static ReplayStartResult<T> Refused(int statusCode, string error) => new(null, statusCode, error);
}

public sealed record ReplayStartResult(ReplaySessionView? Session, int StatusCode, string? Error)
{
    public static ReplayStartResult Refused(int statusCode, string error) => new(null, statusCode, error);
}
