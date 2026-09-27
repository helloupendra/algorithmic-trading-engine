// src/AlgoTrading.Api/Services/FeedFailoverService.cs

using System.Globalization;
using AlgoTrading.Api.Configuration;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Switches the desk from a silent Dhan feed to FYERS during the NSE session —
/// or, in a dry run (the default), says that it would and why.
/// </summary>
/// <remarks>
/// On 24 Sep 2026 the last Dhan tick was 11:27:36 and the first FYERS tick
/// 11:34:06, after someone stopped one feed and started the other by hand.
/// Nothing in the platform acted on the silence: unattended, all 26 runs would
/// have sat blind until the 15:30 square-off. The morning job falls back to
/// FYERS when Dhan delivers nothing after the open (scripts/market-open.sh);
/// this is the same fallback for the rest of the session.
/// <para>
/// <b>What "silent" means.</b> The newest live tick per exchange group in the
/// Redis stream the strategies read (<c>market:ticks</c>), measured exactly as
/// Sentinel's feed-silent rule measures it (sentinel/agents/health.py): the
/// tick's receivedUtc, replays ignored, and the age taken from the later of the
/// newest tick and the session open, so yesterday's last tick never counts.
/// The thresholds are deliberately later than Sentinel's: Sentinel and the
/// runners <i>tell</i> at 90 s, the feed rebuilds its own connection at 120 s
/// (core/live/feed_runner.py, STALL_AFTER_SECONDS), and this <i>acts</i> only
/// on what that did not fix — NSE (or BSE, once it has ticked today) older than
/// <see cref="StaleAfter"/> on <see cref="StaleChecksToAct"/> checks in a row,
/// with the Dhan process up longer than <see cref="DhanGrace"/>. MCX is
/// reported, never acted on: this watches the NSE session only.
/// </para>
/// <para>
/// <b>What it does.</b> Only while Dhan is the feed (the Dhan feed runs and the
/// FYERS feed does not), from 09:20 IST while NSE is open. FYERS is checked
/// with a real call first. Signed in: stop the Dhan feed, start the FYERS feed
/// and its chain poller — the sequence of market-open.sh's own fallback, Dhan's
/// option chain recorder left running — and confirm fresh FYERS ticks within
/// <see cref="VerifyWithin"/>, then one critical alert with the cause. Not
/// signed in: a reminder every <see cref="SignInReminderEvery"/>, and the
/// switch as soon as the sign-in lands. At most one switch a day, recorded
/// under <c>feed.failover.&lt;date&gt;</c> before anything is stopped, so an API
/// restart cannot make a second; it never switches back.
/// </para>
/// <para>
/// <b>Dry run</b> (<c>FeedFailover:DryRun</c>, true unless set false) does all
/// of the watching and none of the switching: it logs
/// <c>FEED FAILOVER (dry run) would switch: …</c> and sends one System-channel
/// message per incident, and writes nothing.
/// </para>
/// </remarks>
public sealed class FeedFailoverService : BackgroundService
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(15);

    /// <summary>Not before this IST time: the morning job proves the feed itself at about 09:17 and falls back on its own.</summary>
    public static readonly TimeSpan WatchFromIst = new(9, 20, 0);

    /// <summary>A group is stale past this age: the feed's own 120 s reconnect has had its chance.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(150);

    /// <summary>Consecutive stale checks before the incident is acted on.</summary>
    public const int StaleChecksToAct = 2;

    /// <summary>A Dhan feed younger than this is still connecting and subscribing.</summary>
    public static readonly TimeSpan DhanGrace = TimeSpan.FromSeconds(180);

    /// <summary>After a switch, fresh FYERS ticks are expected within this.</summary>
    public static readonly TimeSpan VerifyWithin = TimeSpan.FromSeconds(90);

    /// <summary>While Dhan is silent and FYERS is not signed in, the reminder repeats this often.</summary>
    public static readonly TimeSpan SignInReminderEvery = TimeSpan.FromMinutes(10);

    public const string Nse = "NSE";
    public const string Bse = "BSE";
    public const string Mcx = "MCX";
    private static readonly string[] Groups = { Nse, Bse, Mcx };

    private readonly IOptionsMonitor<FeedFailoverOptions> _options;
    private readonly IMarketSessionService _session;
    private readonly IFeedTickSource _ticks;
    private readonly IFeedFailoverFeeds _feeds;
    private readonly IFeedFailoverChecks _checks;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<FeedFailoverService> _logger;
    private readonly TimeProvider _time;

    // All in memory: a restart starts the count again, which only delays an
    // incident by two checks. The one thing that must survive a restart — that
    // today's switch was made — is in system_settings.
    private int _staleChecks;
    private Incident? _incident;
    private Verification? _verifying;
    private DateTime? _lastSeenClosedUtc;
    private DateOnly _seenDay;
    private readonly HashSet<string> _seenToday = new(StringComparer.Ordinal);
    private (int Pid, DateTime FirstSeenUtc)? _dhanFirstSeen;
    private bool _disabledLogged;
    private DateTime? _readFailureLoggedUtc;

    public FeedFailoverService(
        IOptionsMonitor<FeedFailoverOptions> options,
        IMarketSessionService session,
        IFeedTickSource ticks,
        IFeedFailoverFeeds feeds,
        IFeedFailoverChecks checks,
        IServiceScopeFactory scopes,
        ILogger<FeedFailoverService> logger,
        TimeProvider? time = null)
    {
        _options = options;
        _session = session;
        _ticks = ticks;
        _feeds = feeds;
        _checks = checks;
        _scopes = scopes;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    private sealed class Incident(DateTime silentSinceUtc)
    {
        /// <summary>The older of the silent groups' last ticks (or the open): when the silence began.</summary>
        public DateTime SilentSinceUtc { get; } = silentSinceUtc;

        public string Ages { get; set; } = string.Empty;
        public string DhanProcess { get; set; } = string.Empty;

        /// <summary>Dhan's /profile and heartbeat, asked once, when there is first something to say.</summary>
        public string? DhanStatus { get; set; }

        public bool WouldSwitchSent { get; set; }
        public DateTime? SignInReminderUtc { get; set; }
        public bool AlreadyUsedLogged { get; set; }
        public bool StoreUnreadableLogged { get; set; }
    }

    private sealed record Verification(DateTime SwitchedUtc, Incident Incident, string Actions);

    private enum Verdict { Fresh, Stale, Unknown }

    /// <param name="Triggers">NSE always; BSE once it has ticked today; MCX never.</param>
    private sealed record GroupAge(string Group, Verdict Verdict, double? AgeSeconds, FeedTick? Newest, bool Triggers);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.CurrentValue;
        _logger.LogInformation(
            "FEED FAILOVER: {State} — checks every {Every:0} s from {From} IST while NSE is open; acts on NSE/BSE ticks older than {Stale:0} s on {Checks} checks in a row.",
            !options.Enabled ? "switched off (FeedFailover:Enabled=false)"
                : options.DryRun ? "dry run (logs and one message per incident; switches nothing)"
                : "live (switches Dhan to FYERS at most once a day)",
            CheckEvery.TotalSeconds, WatchFromIst.ToString(@"hh\:mm", CultureInfo.InvariantCulture), StaleAfter.TotalSeconds, StaleChecksToAct);

        using var timer = new PeriodicTimer(CheckEvery, _time);
        try
        {
            do
            {
                try
                {
                    await CheckAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // One bad check must not end the watch for the day.
                    _logger.LogWarning(ex, "FEED FAILOVER: a check did not complete; the next one runs in {Every:0} s.", CheckEvery.TotalSeconds);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>One check. Public so the rules can be driven check by check under a fake clock.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled)
        {
            if (!_disabledLogged) _logger.LogInformation("FEED FAILOVER: switched off (FeedFailover:Enabled=false).");
            _disabledLogged = true;
            _staleChecks = 0;
            _incident = null;
            return;
        }
        _disabledLogged = false;

        var now = _time.GetUtcNow().UtcDateTime;

        // A switch made on an earlier check is confirmed before anything else:
        // after it Dhan is stopped, so the watch below would simply stand down.
        if (_verifying is not null)
        {
            await VerifyAsync(_verifying, now, cancellationToken);
            return;
        }

        if (!_session.IsMarketOpen(now, "NSE", "CM"))
        {
            _lastSeenClosedUtc = now;
            CloseIncident("the NSE session is closed");
            return;
        }

        if (IstTime.ToIst(now).TimeOfDay < WatchFromIst)
        {
            CloseIncident("before the watch starts");
            return;
        }

        FeedTickReading reading;
        try
        {
            reading = await _ticks.ReadAsync(now, StaleAfter, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Unknown is not stale: never act on a stream that could not be read.
            _staleChecks = 0;
            if (_readFailureLoggedUtc is not { } logged || now - logged >= SignInReminderEvery)
            {
                _logger.LogWarning("FEED FAILOVER: could not read the tick stream ({Type}: {Message}); not judging the feed until it can.",
                    ex.GetType().Name, ex.Message);
                _readFailureLoggedUtc = now;
            }
            return;
        }

        var ages = Assess(reading, now);
        var agesText = Describe(ages, reading);
        var stale = ages.Where(a => a.Triggers && a.Verdict == Verdict.Stale).ToList();

        if (stale.Count == 0)
        {
            _staleChecks = 0;
            if (_incident is not null && ages.Where(a => a.Triggers).All(a => a.Verdict == Verdict.Fresh))
            {
                _logger.LogInformation("FEED FAILOVER: recovered after {Seconds}s (silent since {Since} IST, fresh at {Now} IST) — {Ages}",
                    Seconds(now - _incident.SilentSinceUtc), IstClock(_incident.SilentSinceUtc), IstClock(now), agesText);
                _incident = null;
            }
            return;
        }

        // Only while Dhan is the feed, and past its start-up.
        var (whyNot, dhanProcess) = await DhanWatchAsync(now, cancellationToken);
        if (whyNot is not null)
        {
            CloseIncident(whyNot);
            return;
        }

        _staleChecks++;
        if (_staleChecks < StaleChecksToAct)
        {
            _logger.LogInformation("FEED FAILOVER: Dhan ticks stale on one check at {Now} IST — {Ages}; acting only if the next check agrees.",
                IstClock(now), agesText);
            return;
        }

        if (_incident is null)
        {
            var silentFor = stale.Max(a => a.AgeSeconds ?? 0);
            _incident = new Incident(now - TimeSpan.FromSeconds(silentFor));
            _logger.LogWarning("FEED FAILOVER: Dhan feed silent at {Now} IST — {Ages}; {Dhan}.", IstClock(now), agesText, dhanProcess);
        }
        _incident.Ages = agesText;
        _incident.DhanProcess = dhanProcess;

        await ActAsync(_incident, options.DryRun, now, cancellationToken);
    }

    // ------------------------------------------------------------------
    // Acting
    // ------------------------------------------------------------------

    private async Task ActAsync(Incident incident, bool dryRun, DateTime now, CancellationToken cancellationToken)
    {
        var today = IstTime.DateOf(now);
        var (used, value) = await SwitchUsedTodayAsync(today, cancellationToken);
        if (used == true)
        {
            if (!incident.AlreadyUsedLogged)
            {
                _logger.LogWarning("FEED FAILOVER: not acting — today's one switch was already made ({Value}).", value);
                incident.AlreadyUsedLogged = true;
            }
            return;
        }

        if (used is null && !dryRun)
        {
            // Unknown is not "not yet": a second switch in a day is the one thing ruled out.
            if (!incident.StoreUnreadableLogged)
            {
                _logger.LogWarning("FEED FAILOVER: not switching — could not read whether today's switch was already made ({Problem}).", value);
                incident.StoreUnreadableLogged = true;
            }
            return;
        }

        // A dry run has said all it has to say about this incident.
        if (dryRun && incident.WouldSwitchSent) return;

        var fyers = await _checks.CheckFyersAsync(cancellationToken);
        if (fyers.State != FyersSignInState.SignedIn)
        {
            await RemindSignInAsync(incident, fyers, dryRun, now, cancellationToken);
            return;
        }

        incident.DhanStatus ??= await _checks.DescribeDhanAsync(cancellationToken);

        if (dryRun)
        {
            incident.WouldSwitchSent = true;
            _logger.LogWarning("FEED FAILOVER (dry run) would switch: {Cause}", Cause(incident, fyers, now));
            _logger.LogInformation("FEED FAILOVER: Dhan says: {Dhan}", incident.DhanStatus);
            await NotifyAsync(
                NotificationCategory.Process,
                NotificationSeverity.Warning,
                "Feed failover (dry run): would switch Dhan → FYERS",
                $"Dhan has sent no tick for over {StaleAfter.TotalSeconds:0} s on {StaleChecksToAct} checks in a row: {incident.Ages}. {incident.DhanProcess}.\n"
                + $"Dhan says: {incident.DhanStatus}.\n"
                + $"FYERS: {fyers.Detail}.\n"
                + $"Would do: stop the Dhan feed, start the FYERS feed and its chain poller, and expect fresh FYERS ticks within {VerifyWithin.TotalSeconds:0} s. Dhan's option chain recorder would keep running.\n"
                + "Dry run: nothing was changed. To switch now: Data → Feeds, stop dhan, start fyers. To let the desk switch by itself: FeedFailover:DryRun=false.");
            return;
        }

        await SwitchAsync(incident, fyers, today, now, cancellationToken);
    }

    private async Task RemindSignInAsync(Incident incident, FyersSignIn fyers, bool dryRun, DateTime now, CancellationToken cancellationToken)
    {
        if (incident.SignInReminderUtc is { } last && now - last < SignInReminderEvery) return;
        incident.SignInReminderUtc = now;
        incident.DhanStatus ??= await _checks.DescribeDhanAsync(cancellationToken);

        _logger.LogWarning("FEED FAILOVER: Dhan silent and FYERS not signed in at {Now} IST — {Fyers}; reminding every {Minutes:0} min. {Ages}",
            IstClock(now), fyers.Detail, SignInReminderEvery.TotalMinutes, incident.Ages);
        await NotifyAsync(
            NotificationCategory.Connector,
            NotificationSeverity.Error,
            "Dhan silent and FYERS not signed in — sign in to FYERS",
            $"Dhan has sent no tick for over {StaleAfter.TotalSeconds:0} s: {incident.Ages}. {incident.DhanProcess}.\n"
            + $"Dhan says: {incident.DhanStatus}.\n"
            + $"FYERS: {fyers.Detail}.\n"
            + (dryRun
                ? "Dry run: after signing in, switch by hand on Data → Feeds (stop dhan, start fyers)."
                : "The desk switches to FYERS as soon as the sign-in lands.")
            + $" Repeated every {SignInReminderEvery.TotalMinutes:0} minutes while Dhan stays silent.");
    }

    private async Task SwitchAsync(Incident incident, FyersSignIn fyers, DateOnly today, DateTime now, CancellationToken cancellationToken)
    {
        // Recorded before anything is stopped: whatever happens next, a restart
        // must find today's switch made and not try again.
        string record = Clean($"{IstClock(now)} IST: switched Dhan → FYERS — {incident.Ages}", 500);
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IProcessSettingsStore>()
                .SetAsync(SystemSettingKeys.FeedFailover(today), record, "feed-failover", CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("FEED FAILOVER: could not record today's switch ({Type}); switching anyway — the strategies are blind. An API restart today could switch a second time.",
                ex.GetType().Name);
        }

        string actions;
        FyersStartOutcome started;
        try
        {
            var stopped = await _feeds.StopDhanAsync($"Feed failover: {Clean(incident.Ages, 160)}", CancellationToken.None);
            started = await _feeds.StartFyersAsync(CancellationToken.None);
            actions = $"{stopped}; {started.Summary}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FEED FAILOVER: the switch did not complete.");
            await NotifyAsync(NotificationCategory.Process, NotificationSeverity.Error,
                "Feed failover did not complete — check the feeds",
                $"Dhan was silent: {incident.Ages}. {incident.DhanProcess}.\nDhan says: {incident.DhanStatus}.\n"
                + $"Stopping Dhan and starting FYERS stopped part-way ({ex.GetType().Name}). Look at Data → Feeds now; today's automatic switch is used.");
            Finish();
            return;
        }

        _logger.LogWarning("FEED FAILOVER switched: {Actions}. Cause: {Cause}", actions, Cause(incident, fyers, now));

        if (!started.FeedRunning)
        {
            await NotifyAsync(NotificationCategory.Process, NotificationSeverity.Error,
                "Feed failover stopped Dhan, but the FYERS feed did not start",
                $"Dhan was silent: {incident.Ages}. {incident.DhanProcess}.\nDhan says: {incident.DhanStatus}.\n"
                + $"Done at {IstClock(now)} IST: {actions}.\n"
                + "Nothing is feeding the strategies. Start a feed on Data → Feeds; today's automatic switch is used.");
            Finish();
            return;
        }

        _verifying = new Verification(now, incident, actions);
    }

    private async Task VerifyAsync(Verification verification, DateTime now, CancellationToken cancellationToken)
    {
        FeedTickReading? reading = null;
        try
        {
            reading = await _ticks.ReadAsync(now, StaleAfter, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Treated as "not yet"; the deadline below still applies.
        }

        var incident = verification.Incident;
        var fresh = reading?.Newest
            .Where(kv => kv.Key is Nse or Bse && kv.Value.AtUtc > verification.SwitchedUtc)
            .OrderBy(kv => kv.Value.AtUtc)
            .Select(kv => (Group: kv.Key, Tick: kv.Value))
            .FirstOrDefault();

        if (fresh is { Tick: not null } arrived)
        {
            int after = Seconds(arrived.Tick.AtUtc - verification.SwitchedUtc);
            _logger.LogInformation("FEED FAILOVER verified: fresh {Group} tick {After}s after the switch ({Symbol} via {Source}).",
                arrived.Group, after, arrived.Tick.Symbol, arrived.Tick.Source);
            _logger.LogInformation("FEED FAILOVER: recovered after {Seconds}s (silent since {Since} IST) — on FYERS.",
                Seconds(now - incident.SilentSinceUtc), IstClock(incident.SilentSinceUtc));
            await NotifyAsync(NotificationCategory.Process, NotificationSeverity.Error,
                "Feed switched from Dhan to FYERS",
                $"Dhan was silent: {incident.Ages}. {incident.DhanProcess}.\n"
                + $"Dhan says: {incident.DhanStatus}.\n"
                + $"Done at {IstClock(verification.SwitchedUtc)} IST: {verification.Actions}.\n"
                + $"FYERS ticks arriving {after} s after the switch ({Clean(arrived.Tick.Symbol, 60)}). Dhan's option chain recorder keeps running.\n"
                + "That was today's one automatic switch, and nothing switches back: return to Dhan by hand on Data → Feeds if you want it.");
            Finish();
            return;
        }

        if (now - verification.SwitchedUtc < VerifyWithin) return;

        _logger.LogWarning("FEED FAILOVER: FYERS started but no fresh tick within {Seconds:0}s of the switch.", VerifyWithin.TotalSeconds);
        await NotifyAsync(NotificationCategory.Process, NotificationSeverity.Error,
            $"Feed switched from Dhan to FYERS, but no FYERS tick within {VerifyWithin.TotalSeconds:0} s",
            $"Dhan was silent: {incident.Ages}. {incident.DhanProcess}.\n"
            + $"Dhan says: {incident.DhanStatus}.\n"
            + $"Done at {IstClock(verification.SwitchedUtc)} IST: {verification.Actions}.\n"
            + "Nothing is feeding the strategies. Check the FYERS feed's log on Data → Feeds (fyers) and the sign-in on Connectors → FYERS; today's automatic switch is used.");
        Finish();
    }

    private void Finish()
    {
        _verifying = null;
        _incident = null;
        _staleChecks = 0;
    }

    private void CloseIncident(string reason)
    {
        _staleChecks = 0;
        if (_incident is null) return;
        _logger.LogInformation("FEED FAILOVER: incident closed without a switch — {Reason}.", reason);
        _incident = null;
    }

    // ------------------------------------------------------------------
    // Reading the situation
    // ------------------------------------------------------------------

    /// <summary>(why not, when Dhan is not the feed or is still starting; the Dhan process in words).</summary>
    private async Task<(string? WhyNot, string Process)> DhanWatchAsync(DateTime now, CancellationToken cancellationToken)
    {
        var dhan = await _feeds.DhanAsync(cancellationToken);
        if (!dhan.Running)
        {
            _dhanFirstSeen = null;
            return ("the Dhan feed is not running", string.Empty);
        }

        if (await _feeds.FyersRunningAsync(cancellationToken))
        {
            return ("the FYERS feed is running", string.Empty);
        }

        // The process's own start time; failing that, when this service first
        // saw that pid, which after an API restart only errs on the side of waiting.
        var started = dhan.StartedUtc ?? FirstSeen(dhan.ProcessId ?? 0, now);
        var up = now - started;
        if (up < DhanGrace)
        {
            return ($"the Dhan feed started {Seconds(up)} s ago (it gets {DhanGrace.TotalSeconds:0} s to connect)", string.Empty);
        }

        return (null, $"Dhan feed pid {dhan.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "?"}, up {Duration(up)}");
    }

    private DateTime FirstSeen(int pid, DateTime now)
    {
        if (_dhanFirstSeen is { } seen && seen.Pid == pid) return seen.FirstSeenUtc;
        _dhanFirstSeen = (pid, now);
        return now;
    }

    private List<GroupAge> Assess(FeedTickReading reading, DateTime now)
    {
        var today = IstTime.DateOf(now);
        if (today != _seenDay)
        {
            _seenDay = today;
            _seenToday.Clear();
        }

        var anchor = SessionAnchor(today);
        var ages = new List<GroupAge>(Groups.Length);
        foreach (var group in Groups)
        {
            reading.Newest.TryGetValue(group, out var tick);
            if (tick is not null && tick.AtUtc >= anchor) _seenToday.Add(group);

            // Sentinel's rule: from the later of the newest tick and the open; a
            // group missing from what was read is as old as the oldest entry
            // read when that is past the line, and unknown when it is not.
            DateTime? reference = tick is not null ? Max(tick.AtUtc, anchor)
                : reading.Exhausted ? anchor
                : reading.OldestReadUtc is { } oldest && now - oldest > StaleAfter ? Max(oldest, anchor)
                : null;

            double? age = reference is { } r ? Math.Max(0, (now - r).TotalSeconds) : null;
            var verdict = age is null ? Verdict.Unknown
                : age > StaleAfter.TotalSeconds ? Verdict.Stale
                : Verdict.Fresh;

            // NSE always (the desk trades NIFTY and BANKNIFTY every day). BSE once
            // it has ticked today — on 24 Sep at 15:18 SENSEX alone went quiet —
            // but never on a day nothing BSE is subscribed. MCX is not this
            // session's business.
            bool triggers = group == Nse || (group == Bse && _seenToday.Contains(Bse));
            ages.Add(new GroupAge(group, verdict, age, tick, triggers));
        }
        return ages;
    }

    /// <summary>Today's 09:15 IST open, or later if the session was seen closed after it (a late or special open).</summary>
    private DateTime SessionAnchor(DateOnly today)
    {
        var open = IstTime.FromIst(today.ToDateTime(TimeOnly.FromTimeSpan(IstTime.SessionOpen)));
        return _lastSeenClosedUtc is { } closed && closed > open ? closed : open;
    }

    private static string Describe(IReadOnlyList<GroupAge> ages, FeedTickReading reading)
    {
        return string.Join(", ", ages.Select(a =>
        {
            if (a.Verdict == Verdict.Unknown)
            {
                return $"{a.Group} not in the newest {reading.Scanned} entries";
            }

            string age = $"{Seconds(TimeSpan.FromSeconds(a.AgeSeconds ?? 0))} s";
            if (a.Verdict == Verdict.Fresh) return $"{a.Group} {age}";

            string seen = a.Newest is { } t
                ? $"newest {IstClock(t.AtUtc)} IST, {Clean(t.Symbol, 40)} via {Clean(t.Source, 20)}"
                : "no tick since the open";
            if (!a.Triggers)
            {
                return a.Group == Bse ? $"BSE {age} (no BSE tick yet today, so not watched)" : $"{a.Group} {age}";
            }
            return $"{a.Group} silent {age} ({seen})";
        }));
    }

    private static string Cause(Incident incident, FyersSignIn fyers, DateTime now)
        => $"{incident.Ages}; {incident.DhanProcess}; FYERS: {fyers.Detail}; at {IstClock(now)} IST";

    private async Task<(bool? Used, string? Value)> SwitchUsedTodayAsync(DateOnly today, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var value = await scope.ServiceProvider.GetRequiredService<IProcessSettingsStore>()
                .GetAsync(SystemSettingKeys.FeedFailover(today), cancellationToken);
            return (!string.IsNullOrWhiteSpace(value), value);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task NotifyAsync(NotificationCategory category, NotificationSeverity severity, string title, string message)
    {
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISystemNotifier>()
                .NotifyAsync(category, severity, title, message, cancellationToken: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FEED FAILOVER: could not send the notification '{Title}'.", title);
        }
    }

    // ------------------------------------------------------------------
    // Words
    // ------------------------------------------------------------------

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static int Seconds(TimeSpan span) => (int)Math.Max(0, Math.Round(span.TotalSeconds));

    private static string IstClock(DateTime utc)
        => IstTime.ToIst(utc).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan span)
    {
        int s = Seconds(span);
        if (s < 300) return $"{s} s";
        if (s < 3600) return $"{s / 60} min";
        return $"{s / 3600} h {s % 3600 / 60} min";
    }

    /// <summary>
    /// Vendor text made safe for a Telegram HTML message (the subscriber sends
    /// with parse_mode HTML, and a stray &lt; makes Telegram refuse the whole
    /// message) and short enough to read.
    /// </summary>
    public static string Clean(string? text, int limit)
    {
        var flat = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Replace('<', '‹').Replace('>', '›').Replace("&", "and");
        return flat.Length <= limit ? flat : flat[..(limit - 1)] + "…";
    }
}
