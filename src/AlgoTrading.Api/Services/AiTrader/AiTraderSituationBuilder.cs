using System.Globalization;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.AiTrader;

/// <summary>
/// Fills <c>ai_trader_situations</c> from the stored 1-minute index candles (<c>candles</c>, resolution "1": Dhan's
/// index history from Aug 2020 for NIFTY and Aug 2021 for BANKNIFTY, SENSEX and India VIX, then the nightly
/// archive's): each index's moments of a day (<see cref="SituationMath.Slots"/>) with what followed.
/// </summary>
/// <remarks>
/// <para>
/// Day by day, oldest first, one day in memory at a time: each index's last <see cref="SituationMath.MinutesToRead"/>
/// minutes to the day's end (the day and the sessions before it, for the EMAs and the previous close) and India
/// VIX's. A day's rows replace any it had, and the last day done (<see cref="LastDayKey"/> in system_settings) is
/// saved with them, so a run stopped anywhere carries on from the next day and a day is never half written. Days
/// with no bars (weekends, holidays, before an index's history) write nothing.
/// </para>
/// <para>
/// The last day it builds is the nightly candle archive's last day (<see cref="NightlyArchiveService.LastArchivedDayKey"/>),
/// so a day is built only once its candles are complete; with no archive, yesterday. It never runs in a trading
/// day's session (<see cref="InSession"/>), and pauses <see cref="PauseBetweenDays"/> after each day it wrote: the
/// server is shared with the live desk.
/// </para>
/// </remarks>
public sealed class AiTraderSituationBuilder(
    TradingDbContext db,
    SituationCalendar calendar,
    IMarketSessionService sessions,
    AiTraderSituationsStatus status,
    ILogger<AiTraderSituationBuilder> logger,
    TimeProvider? time = null)
{
    /// <summary>The last day built, "yyyy-MM-dd". Absent until the backfill is started.</summary>
    public const string LastDayKey = "aitrader.situations.lastDay";

    /// <summary>The candle resolution read: 1-minute.</summary>
    public const string MinuteResolution = "1";

    /// <summary>A progress line in the log every this many days with bars.</summary>
    public const int LogEveryDays = 100;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The wait after each day it wrote, so the database and CPU are left to the desk in between.</summary>
    public TimeSpan PauseBetweenDays { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 08:45 to 15:45 IST on a trading day: from the morning job until a quarter of an hour after the NSE close, both
    /// from the session service, so an exchange holiday is not a session and a weekday is not by itself a trading day.
    /// </summary>
    public static bool InSession(DateTime nowUtc, IMarketSessionService sessions)
    {
        var info = sessions.GetSessionInfo(nowUtc, "NSE", "CM");
        return info.IsTradingDay && nowUtc >= info.SessionOpenUtc.AddMinutes(-30) && nowUtc < info.SessionCloseUtc.AddMinutes(15);
    }

    /// <summary>Starts (or restarts) the history from <paramref name="from"/>, or from the first stored 1-minute index candle.</summary>
    public async Task<DateOnly?> StartAsync(DateOnly? from, CancellationToken cancellationToken)
    {
        var first = from ?? await FirstDayAsync(cancellationToken);
        if (first is not DateOnly start)
        {
            logger.LogWarning("AI Trader situations: no 1-minute index candles are stored, so there is no history to build");
            status.Note("No 1-minute index candles are stored: nothing to build.");
            return null;
        }

        await SaveLastDayAsync(start.AddDays(-1), cancellationToken);
        logger.LogInformation("AI Trader situations: the history is built from {From}", start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return start;
    }

    /// <summary>
    /// Builds the days after the last one done, up to the archive's last day, while <paramref name="mayContinue"/>
    /// allows (asked before each day). Nothing before the backfill is started.
    /// </summary>
    public async Task<SituationRun> RunAsync(Func<bool> mayContinue, CancellationToken cancellationToken)
    {
        var last = await LastDayAsync(cancellationToken);
        if (last is not DateOnly done)
        {
            status.NotStarted();
            return new SituationRun(SituationRunState.NotStarted, null, 0, 0);
        }

        var through = await ThroughAsync(cancellationToken);
        if (done >= through)
        {
            status.UpToDate(done, through, _time.GetUtcNow().UtcDateTime);
            return new SituationRun(SituationRunState.UpToDate, done, 0, 0);
        }

        var started = _time.GetUtcNow().UtcDateTime;
        int days = 0, rows = 0, withBars = 0;
        status.Running(done, through, started);
        logger.LogInformation("AI Trader situations: building {From} to {Through} ({Days} days)",
            Ymd(done.AddDays(1)), Ymd(through), through.DayNumber - done.DayNumber);

        for (var day = done.AddDays(1); day <= through; day = day.AddDays(1))
        {
            if (!mayContinue())
            {
                logger.LogInformation("AI Trader situations: paused after {Day} ({Days} days, {Rows} rows this run); it carries on outside the session",
                    Ymd(day.AddDays(-1)), days, rows);
                status.Paused(day.AddDays(-1), through);
                return new SituationRun(SituationRunState.Paused, day.AddDays(-1), days, rows);
            }

            int written = await BuildDayAsync(day, cancellationToken);
            days++;
            rows += written;
            status.DayDone(day, written);
            if (written == 0) continue;

            if (++withBars % LogEveryDays == 0)
            {
                logger.LogInformation("AI Trader situations: built through {Day}, {Rows} rows this run; {Left} days to go",
                    Ymd(day), rows, through.DayNumber - day.DayNumber);
            }

            if (PauseBetweenDays > TimeSpan.Zero) await Task.Delay(PauseBetweenDays, _time, cancellationToken);
        }

        var took = _time.GetUtcNow().UtcDateTime - started;
        logger.LogInformation("AI Trader situations: built through {Through}: {Days} days, {Rows} rows in {Minutes:0.0} min",
            Ymd(through), days, rows, took.TotalMinutes);
        status.UpToDate(through, through, _time.GetUtcNow().UtcDateTime);
        return new SituationRun(SituationRunState.UpToDate, through, days, rows);
    }

    /// <summary>One day's rows for every index, replacing any it had, saved with the day as the last one done. Returns the rows written.</summary>
    /// <remarks>
    /// An index is read only when it has a bar that day (one cheap look first, so a weekend reads nothing), and not on
    /// a day the holiday calendar knows to be closed, nor when the day's bars never moved: a quote repeated on a closed
    /// day is not a session.
    /// </remarks>
    public async Task<int> BuildDayAsync(DateOnly day, CancellationToken cancellationToken)
    {
        var dayStart = IstTime.StartOfDayUtc(day);
        var dayEnd = IstTime.StartOfDayUtc(day.AddDays(1));
        var built = _time.GetUtcNow().UtcDateTime;
        var rows = new List<AiTraderSituation>();
        List<LiveBarResponse>? vix = null;
        foreach (var (name, spot, exchange) in MarketBriefBuilder.Indices)
        {
            var info = sessions.GetSessionInfo(IstTime.MiddayUtc(day), exchange, "FO");
            if (!info.IsTradingDay && info.CalendarWarning is null) continue;
            if (!await db.Candles.AsNoTracking().AnyAsync(c => c.Symbol == spot && c.Resolution == MinuteResolution
                                                              && c.TimeStampUtc >= dayStart && c.TimeStampUtc < dayEnd, cancellationToken))
            {
                continue;
            }

            var minutes = SituationMath.Session(await ReadAsync(spot, dayEnd, cancellationToken));
            var today = minutes.Where(b => b.BarStartUtc >= dayStart).ToList();
            if (today.Count == 0 || today.Max(b => b.High) == today.Min(b => b.Low)) continue;

            vix ??= await ReadAsync(MarketBriefBuilder.VixSymbol, dayEnd, cancellationToken);
            int? dte = await calendar.TradingDaysToExpiryAsync(name, exchange, day, cancellationToken);
            foreach (var slot in SituationMath.Slots)
            {
                var moment = IstTime.FromIst(day.ToDateTime(slot));
                var (facts, _) = SituationMath.Features(moment, minutes, vix, dte);
                if (facts is null) continue;
                rows.Add(SituationMath.Row(name, facts, SituationMath.Outcomes(moment, minutes, facts.Price), built));
            }
        }

        // By index and day, so the unique index finds them.
        var names = MarketBriefBuilder.Indices.Select(i => i.Name).ToList();
        var old = await db.AiTraderSituations.Where(s => names.Contains(s.Underlying) && s.Day == day).ToListAsync(cancellationToken);
        db.AiTraderSituations.RemoveRange(old);
        db.AiTraderSituations.AddRange(rows);
        await SaveLastDayAsync(day, cancellationToken);
        db.ChangeTracker.Clear();
        return rows.Count;
    }

    /// <summary>The last day built; null before the backfill is started.</summary>
    public async Task<DateOnly?> LastDayAsync(CancellationToken cancellationToken)
    {
        var value = await db.SystemSettings.AsNoTracking().Where(s => s.Key == LastDayKey).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
    }

    /// <summary>The last day whose candles are complete: the nightly archive's last day, else yesterday; never after today (IST).</summary>
    public async Task<DateOnly> ThroughAsync(CancellationToken cancellationToken)
    {
        var today = IstTime.DateOf(_time.GetUtcNow().UtcDateTime);
        var value = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == NightlyArchiveService.LastArchivedDayKey).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);
        var archived = DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : today.AddDays(-1);
        return archived < today ? archived : today;
    }

    /// <summary>The IST day of the first stored 1-minute candle of any of the indices.</summary>
    public async Task<DateOnly?> FirstDayAsync(CancellationToken cancellationToken)
    {
        DateTime? first = null;
        foreach (var (_, spot, _) in MarketBriefBuilder.Indices)
        {
            var at = await db.Candles.AsNoTracking()
                .Where(c => c.Symbol == spot && c.Resolution == MinuteResolution)
                .MinAsync(c => (DateTime?)c.TimeStampUtc, cancellationToken);
            if (at is DateTime a && (first is null || a < first)) first = a;
        }

        return first is DateTime f ? IstTime.DateOf(f) : null;
    }

    /// <summary>A symbol's last <see cref="SituationMath.MinutesToRead"/> 1-minute candles before <paramref name="beforeUtc"/>, oldest first.</summary>
    private async Task<List<LiveBarResponse>> ReadAsync(string symbol, DateTime beforeUtc, CancellationToken cancellationToken)
    {
        var rows = await db.Candles.AsNoTracking()
            .Where(c => c.Symbol == symbol && c.Resolution == MinuteResolution && c.TimeStampUtc < beforeUtc)
            .OrderByDescending(c => c.TimeStampUtc)
            .Take(SituationMath.MinutesToRead)
            .Select(c => new LiveBarResponse
            {
                Symbol = c.Symbol, Resolution = "1m", BarStartUtc = c.TimeStampUtc, Open = c.Open, High = c.High, Low = c.Low, Close = c.Close,
            })
            .ToListAsync(cancellationToken);
        rows.Reverse();
        foreach (var b in rows) b.BarStartUtc = DateTime.SpecifyKind(b.BarStartUtc, DateTimeKind.Utc);
        return rows;
    }

    private async Task SaveLastDayAsync(DateOnly day, CancellationToken cancellationToken)
    {
        var setting = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == LastDayKey, cancellationToken);
        if (setting is null)
        {
            setting = new SystemSetting { Key = LastDayKey, UpdatedBy = nameof(AiTraderSituationBuilder), Reason = "AI Trader base rates: the last day built" };
            db.SystemSettings.Add(setting);
        }

        setting.Value = Ymd(day);
        setting.UpdatedUtc = _time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Ymd(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

public enum SituationRunState
{
    NotStarted,
    Waiting,
    Paused,
    UpToDate,
}

/// <summary>A pass of the builder: how it ended, the last day done, and the days and rows it wrote.</summary>
public sealed record SituationRun(SituationRunState State, DateOnly? LastDay, int Days, int Rows);

/// <summary>
/// The backfill's state for the admin endpoint (<c>GET api/AiTrader/situations</c>), and a start asked for by an
/// admin (<c>POST api/AiTrader/situations/backfill</c>), which the hosted service takes on its next pass.
/// </summary>
public sealed class AiTraderSituationsStatus
{
    private readonly object _lock = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private bool _requested;
    private DateOnly? _requestedFrom;
    private string _state = "not started";
    private string? _message;
    private DateOnly? _lastDay, _through;
    private int _days, _rows;
    private DateTime? _startedUtc, _finishedUtc, _failedUtc;
    private string? _error;

    /// <summary>A start asked for: from the first stored candle (<paramref name="from"/> null) or from that day.</summary>
    public void Request(DateOnly? from)
    {
        lock (_lock)
        {
            _requested = true;
            _requestedFrom = from;
        }

        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already woken.
        }
    }

    public bool HasRequest
    {
        get
        {
            lock (_lock) return _requested;
        }
    }

    /// <summary>The start asked for, once: (true, from) when there is one.</summary>
    public (bool Requested, DateOnly? From) TakeRequest()
    {
        lock (_lock)
        {
            var taken = (_requested, _requestedFrom);
            _requested = false;
            _requestedFrom = null;
            return taken;
        }
    }

    /// <summary>Waits until <paramref name="timeout"/> passes or a start is asked for.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => await _wake.WaitAsync(timeout, cancellationToken);

    public void NotStarted() => Set("not started", "Not started: POST api/AiTrader/situations/backfill builds the history.");

    public void Waiting(DateTime nowUtc) => Set("waiting", string.Create(CultureInfo.InvariantCulture,
        $"Waiting: it does not run from 08:45 to 15:45 IST on a trading day (now {IstTime.ToIst(nowUtc):HH:mm})."));

    public void Running(DateOnly lastDay, DateOnly through, DateTime startedUtc)
    {
        lock (_lock)
        {
            (_state, _message, _lastDay, _through, _days, _rows, _startedUtc, _finishedUtc) = ("building", null, lastDay, through, 0, 0, startedUtc, null);
        }
    }

    public void DayDone(DateOnly day, int rows)
    {
        lock (_lock)
        {
            _lastDay = day;
            _days++;
            _rows += rows;
            _error = null;
        }
    }

    public void Paused(DateOnly lastDay, DateOnly through)
    {
        lock (_lock)
        {
            (_state, _message, _lastDay, _through) = ("paused", "Paused for the session; it carries on after 15:45 IST.", lastDay, through);
        }
    }

    public void UpToDate(DateOnly lastDay, DateOnly through, DateTime nowUtc)
    {
        lock (_lock)
        {
            if (_state == "building") _finishedUtc = nowUtc;
            (_state, _message, _lastDay, _through) = ("up to date", null, lastDay, through);
        }
    }

    public void Failed(DateTime nowUtc, Exception error)
    {
        lock (_lock)
        {
            (_state, _failedUtc, _error) = ("failing", nowUtc, error.Message);
            _message = "The last pass failed; it is tried again within 10 minutes.";
        }
    }

    public void Note(string message) => Set(_state, message);

    public AiTraderSituationsProgress Snapshot()
    {
        lock (_lock)
        {
            return new AiTraderSituationsProgress(_state, _message, _lastDay?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                _through?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), _days, _rows, _startedUtc, _finishedUtc, _error, _failedUtc, _requested);
        }
    }

    private void Set(string state, string? message)
    {
        lock (_lock)
        {
            (_state, _message) = (state, message);
        }
    }
}

/// <summary>The backfill as the endpoint shows it; the days and rows are this process's current or last run.</summary>
public sealed record AiTraderSituationsProgress(
    string State, string? Message, string? LastDay, string? Through, int DaysThisRun, int RowsThisRun,
    DateTime? StartedUtc, DateTime? FinishedUtc, string? LastError, DateTime? LastErrorUtc, bool StartRequested);

/// <summary>
/// Keeps <c>ai_trader_situations</c> built (<see cref="AiTraderSituationBuilder"/>): every 10 minutes, or at once when
/// an admin asks, outside 08:45–15:45 IST on trading days, it builds the days after the last one done up to the
/// nightly candle archive's last day. So the one-off backfill an admin starts and the nightly day after the archive
/// are the same pass. Nothing runs until the backfill is first started. After a pass it loads the base rates' memory
/// (<see cref="AiTraderSituationLibrary"/>) with what it wrote, so the session's looks read no table.
/// </summary>
public sealed class AiTraderSituationsService(
    IServiceScopeFactory scopes,
    AiTraderSituationsStatus status,
    AiTraderSituationLibrary library,
    IMarketSessionService sessions,
    ILogger<AiTraderSituationsService> logger,
    TimeProvider? time = null) : BackgroundService
{
    /// <summary>After the API's start (migrations, the calendars, the reconcilers).</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(3);

    public static readonly TimeSpan Every = TimeSpan.FromMinutes(10);

    /// <summary>A pass that keeps failing is a warning at most this often; its other failures are information.</summary>
    private static readonly TimeSpan WarnEvery = TimeSpan.FromHours(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTime? _warnedUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, _time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // An unhandled exception in a hosted service stops the host. The day is tried again next pass.
                    var now = _time.GetUtcNow().UtcDateTime;
                    status.Failed(now, ex);
                    if (_warnedUtc is null || now - _warnedUtc >= WarnEvery)
                    {
                        _warnedUtc = now;
                        logger.LogWarning(ex, "AI Trader situations: building the history failed; it is tried again within 10 minutes");
                    }
                    else
                    {
                        logger.LogInformation("AI Trader situations: building the history failed again: {Error}", ex.Message);
                    }
                }

                await status.WaitAsync(Every, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The API is stopping.
        }
    }

    /// <summary>One pass: a start asked for is taken first; then, outside the session, the days that are due. Public for tests.</summary>
    public async Task<SituationRun> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var builder = scope.ServiceProvider.GetRequiredService<AiTraderSituationBuilder>();
        var (requested, from) = status.TakeRequest();
        if (requested) await builder.StartAsync(from, cancellationToken);

        var now = _time.GetUtcNow().UtcDateTime;
        if (AiTraderSituationBuilder.InSession(now, sessions))
        {
            var last = await builder.LastDayAsync(cancellationToken);
            if (last is null) status.NotStarted();
            else status.Waiting(now);
            return new SituationRun(SituationRunState.Waiting, last, 0, 0);
        }

        // Stops before a day when the session starts or another start is asked for (taken on the next pass).
        var run = await builder.RunAsync(
            () => !cancellationToken.IsCancellationRequested && !status.HasRequest && !AiTraderSituationBuilder.InSession(_time.GetUtcNow().UtcDateTime, sessions),
            cancellationToken);
        _warnedUtc = null;
        if (run.Days > 0) library.Invalidate();
        if (run.State != SituationRunState.Paused) await library.WarmAsync(cancellationToken);
        return run;
    }
}
