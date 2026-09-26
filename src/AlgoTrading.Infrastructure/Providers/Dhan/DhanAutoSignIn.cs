using System.Globalization;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Providers.Dhan;

/// <summary>
/// When the desk should take a new Dhan token by itself. Pure, so the rule can
/// be read and tested without a clock or a database.
/// </summary>
/// <remarks>
/// <para>
/// Weekdays only (IST), and never before <see cref="DhanAutoSignInSettings.MorningFromIst"/>
/// (08:00). Nothing streams on Dhan before the 08:45 job, and anchoring every
/// automatic sign-in at or after 08:00 means each day's token is taken at the
/// same hour and lasts the whole session. When a token ending within
/// <see cref="EndingMargin"/> was replaced at any hour, the sign-in drifted ten
/// minutes earlier every day, and a token already dead at midnight was tried
/// for at 00:00:30, which could spend the day's tries before the morning.
/// </para>
/// <para>
/// From 08:00 the rule is "only when needed", never "every so often". Dhan's
/// documentation says <c>RenewToken</c> expires the token it renews, and says
/// nothing either way about whether a fresh PIN + TOTP sign-in ends the previous
/// token. A sign-in in the middle of the session could therefore cut off a feed
/// that was streaming happily. So a new token is taken only
/// </para>
/// <list type="bullet">
/// <item>when there is no sign-in that is still valid, or it ends within
/// <see cref="EndingMargin"/> — nothing is working on it, so nothing can be cut
/// off; or</item>
/// <item>in the morning window before the 08:45 job starts the feeds, when the
/// token would end before tonight's MCX close. A Connect pressed at 13:00 gives a
/// token that dies at 13:00 the next day, in the middle of the session; this is
/// what replaces it before anything depends on it.</item>
/// </list>
/// <para>
/// The feeds do not run at the weekend, and a token taken on Saturday would be
/// dead by Monday anyway. The morning job asks for a sign-in itself when it
/// finds none, so a special session is still covered.
/// </para>
/// </remarks>
public static class DhanAutoSignInPolicy
{
    private static readonly TimeSpan IstOffset = TimeSpan.FromMinutes(330);

    /// <summary>A token this close to its end is treated as already gone.</summary>
    public static readonly TimeSpan EndingMargin = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Why a sign-in is needed now, or null when it is not.
    /// <paramref name="validUntilUtc"/> is when the current sign-in ends, or null
    /// when there is none that is still valid.
    /// </summary>
    public static string? ReasonToSignIn(DateTime utcNow, DateTime? validUntilUtc, DhanAutoSignInSettings settings)
    {
        var ist = utcNow + IstOffset;
        if (ist.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return null;

        var time = ist.TimeOfDay;
        if (time < settings.MorningFromIst) return null;

        if (validUntilUtc is not { } until)
            return "Dhan had no valid sign-in";
        if (until - utcNow < EndingMargin)
            return $"the token was ending at {Ist(until)} IST";

        if (time < settings.MorningUntilIst)
        {
            // Plus the margin: a token ending at 00:05 counts as gone from 23:55
            // and would be replaced then, with the evening feed still up (MCX
            // closes at 23:55 in winter).
            var endOfDayUtc = ist.Date.AddHours(23).AddMinutes(59) - IstOffset;
            if (until < endOfDayUtc + EndingMargin)
                return $"the token would end at {Ist(until)} IST, before tonight's session closes";
        }

        return null;
    }

    private static string Ist(DateTime utc) => (utc + IstOffset).ToString("HH:mm");
}

/// <summary>
/// The automatic sign-in stopped for the rest of an IST day, and why. Saved under
/// <see cref="SystemSettingKeys.DhanAutoSignInStopped"/> as
/// "2026-09-29: Dhan refused the PIN or the code", so an API restart that day
/// finds it. The reason is one of the fixed phrases the state writes, never
/// anything Dhan or the request said.
/// </summary>
public sealed record DhanAutoSignInStop(DateOnly Day, string Reason)
{
    public string Stored => $"{Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}: {Reason}";

    /// <summary>The stored value read back, or null when it is absent or not in that shape.</summary>
    public static DhanAutoSignInStop? FromStored(string? stored)
    {
        var parts = (stored ?? string.Empty).Split(": ", 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
               && parts[1].Length > 0
               && DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? new DhanAutoSignInStop(day, parts[1])
            : null;
    }
}

/// <summary>What the automatic sign-in last did, shared by the worker, the endpoint and the connector page.</summary>
/// <remarks>
/// In memory; the stop for the day is also saved (<see cref="DhanAutoSignInService"/>),
/// because this object does not survive the API restart the morning job makes.
/// </remarks>
public sealed class DhanAutoSignInState
{
    /// <summary>Tries per IST day when Dhan cannot be reached. A refusal, or a missing value, stops the day at once.</summary>
    public const int MaxTriesPerDay = 3;

    /// <summary>The gap after an unreachable Dhan before the machine tries again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan IstOffset = TimeSpan.FromMinutes(330);
    private readonly object _lock = new();
    private DateOnly _day;
    private int _triesToday;
    private string? _stoppedBecause;
    private long? _lastCodeStep;

    /// <summary>One sign-in at a time: the worker and a button pressed at the same moment would spend two codes.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public DateTime? LastAttemptUtc { get; private set; }
    public bool? LastOk { get; private set; }
    public string? LastMessage { get; private set; }

    /// <summary>"automatic", "morning job" or "console".</summary>
    public string? LastTrigger { get; private set; }

    public DateTime? LastExpiresUtc { get; private set; }

    /// <summary>The 30-second TOTP step of the last code sent to Dhan, so the next one comes from another.</summary>
    public long? LastCodeStep
    {
        get { lock (_lock) return _lastCodeStep; }
    }

    public void CodeSent(long step)
    {
        lock (_lock) _lastCodeStep = step;
    }

    /// <summary>True once Dhan has refused today, or the tries are used up: no more automatic tries until tomorrow.</summary>
    public bool StoppedFor(DateTime utcNow)
    {
        lock (_lock)
        {
            Roll(utcNow);
            return _stoppedBecause is not null;
        }
    }

    /// <summary>
    /// Whether a machine (not a person) may try now, and if not, why. A person
    /// pressing the button is never held back: they are the one who can see
    /// whether the PIN was just corrected.
    /// </summary>
    public bool MayTryAutomatically(DateTime utcNow, out string why)
    {
        lock (_lock)
        {
            Roll(utcNow);
            if (_stoppedBecause is { } because)
            {
                why = $"stopped for today ({because})";
                return false;
            }
            if (LastOk == false && LastAttemptUtc is { } last && utcNow - last < RetryAfter)
            {
                why = $"the last try failed at {last:HH:mm} UTC; waiting {RetryAfter.TotalMinutes:0} minutes";
                return false;
            }
            why = string.Empty;
            return true;
        }
    }

    public void Succeeded(DateTime utcNow, string trigger, string message, DateTime expiresUtc)
    {
        lock (_lock)
        {
            Roll(utcNow);
            LastAttemptUtc = utcNow;
            LastOk = true;
            LastTrigger = trigger;
            LastMessage = message;
            LastExpiresUtc = expiresUtc;
            _stoppedBecause = null;
        }
    }

    /// <returns>
    /// The stop, when this failure used up the day: say so once, loudly, and
    /// save it. Null when automatic tries go on, or had already stopped.
    /// </returns>
    public DhanAutoSignInStop? Failed(DateTime utcNow, string trigger, string message, DhanSignInFailure failure)
    {
        lock (_lock)
        {
            Roll(utcNow);
            LastAttemptUtc = utcNow;
            LastOk = false;
            LastTrigger = trigger;
            LastMessage = message;
            _triesToday++;
            if (_stoppedBecause is not null) return null;

            // Fixed phrases, never the message: this is saved, and the message
            // carries whatever reason Dhan gave.
            _stoppedBecause = failure switch
            {
                DhanSignInFailure.Refused => "Dhan refused the PIN or the code",
                DhanSignInFailure.NotSetUp => "a value it needs is missing or malformed",
                _ when _triesToday >= MaxTriesPerDay => $"{MaxTriesPerDay} tries failed",
                _ => null,
            };
            return _stoppedBecause is null ? null : new DhanAutoSignInStop(_day, _stoppedBecause);
        }
    }

    /// <summary>
    /// A stop saved before a restart, taken back if it is from today (IST).
    /// Returns whether it was.
    /// </summary>
    public bool Restore(DhanAutoSignInStop stop, DateTime utcNow)
    {
        lock (_lock)
        {
            Roll(utcNow);
            if (stop.Day != _day) return false;
            _stoppedBecause ??= stop.Reason;
            // The panel shows the last message beside "stopped for today"; after
            // a restart there is none, and the reason is the useful part.
            LastMessage ??= $"Stopped before the API restarted: {stop.Reason}.";
            return true;
        }
    }

    private void Roll(DateTime utcNow)
    {
        var today = DateOnly.FromDateTime(utcNow + IstOffset);
        if (today == _day) return;
        _day = today;
        _triesToday = 0;
        _stoppedBecause = null;
    }
}

/// <summary>
/// Takes the token, records what happened and says so on Telegram. Shared by
/// the background worker, the morning job's call and the console's button, so
/// all three are one code path.
/// </summary>
public sealed class DhanAutoSignInService
{
    /// <summary>A machine asking within this long of a sign-in is answered with that one, not a new code.</summary>
    public static readonly TimeSpan RecentSuccess = TimeSpan.FromMinutes(2);

    private const string ConsoleTrigger = "console";
    private static readonly TimeSpan IstOffset = TimeSpan.FromMinutes(330);

    private readonly DhanLoginFlow _login;
    private readonly DhanAutoSignInState _state;
    private readonly IProcessSettingsStore _settings;
    private readonly ISystemNotifier _notifier;
    private readonly ILogger<DhanAutoSignInService> _logger;
    private readonly TimeProvider _time;

    public DhanAutoSignInService(
        DhanLoginFlow login,
        DhanAutoSignInState state,
        IProcessSettingsStore settings,
        ISystemNotifier notifier,
        ILogger<DhanAutoSignInService> logger,
        TimeProvider? time = null)
    {
        _login = login;
        _state = state;
        _settings = settings;
        _notifier = notifier;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// One sign-in. Never throws for a failed sign-in: the result says what
    /// happened, and the state and Telegram have both been told. For a machine
    /// (any <paramref name="trigger"/> but "console"), Dhan may not be asked at
    /// all: <see cref="DhanAutoSignInResult.Tried"/> is then false.
    /// </summary>
    public async Task<DhanAutoSignInResult> SignInAsync(string trigger, string reason, CancellationToken cancellationToken)
    {
        await _state.Gate.WaitAsync(cancellationToken);
        try
        {
            // Decided again inside the gate. The worker and the morning job each
            // decided before waiting for it, so the second in could send a code
            // after the first had just signed in, or just been refused.
            if (trigger != ConsoleTrigger && await HoldBackAsync(cancellationToken) is { } held) return held;

            DhanSignIn signIn;
            try
            {
                signIn = await _login.SignInWithTotpAsync(_state.LastCodeStep, _state.CodeSent, cancellationToken);
            }
            catch (DhanSignInException ex)
            {
                return await FailedAsync(trigger, ex.Message, ex.Failure, cancellationToken);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                // Anything else used to escape uncaught: no failure recorded, so
                // no pause and no daily cap, and the worker would ask again a
                // minute later. Recorded as a failure worth retrying, within the cap.
                // The type and stack only: the exception's text can carry the
                // request, and the request carries the PIN.
                _logger.LogWarning("Dhan automatic sign-in ({Trigger}) failed unexpectedly ({Type}) at {Stack}",
                    trigger, ex.GetType().Name, ex.StackTrace);
                return await FailedAsync(trigger, $"The Dhan sign-in failed unexpectedly ({ex.GetType().Name}).",
                    DhanSignInFailure.Unreachable, cancellationToken);
            }

            string until = (signIn.ExpiresUtc + IstOffset).ToString("ddd HH:mm");
            string message = $"Signed in with the PIN and a TOTP code ({reason}); token valid until {until} IST.";
            _state.Succeeded(Now, trigger, message, signIn.ExpiresUtc);
            await ClearStopAsync();
            await _notifier.NotifyAsync(NotificationCategory.Connector, NotificationSeverity.Success,
                "Dhan signed in by itself", message, cancellationToken: cancellationToken);
            return new DhanAutoSignInResult(true, message, signIn.ExpiresUtc, null);
        }
        finally
        {
            _state.Gate.Release();
        }
    }

    /// <summary>
    /// At startup: a stop saved earlier today, back into the state, so the panel
    /// and the worker's first look see it. A read that fails is only logged:
    /// every automatic try reads it again first, and none is made while it cannot be.
    /// </summary>
    public async Task RestoreStopAsync(CancellationToken cancellationToken)
    {
        DhanAutoSignInStop? stored;
        try
        {
            stored = await ReadStopAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not read whether the Dhan automatic sign-in was stopped earlier today; no automatic try is made until it can be read.");
            return;
        }

        if (stored is not null && _state.Restore(stored, Now))
            _logger.LogWarning("The Dhan automatic sign-in stays stopped for today across the restart: {Reason}.", stored.Reason);
    }

    /// <summary>What a machine gets instead of a new code, or null when it may send one.</summary>
    private async Task<DhanAutoSignInResult?> HoldBackAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        if (_state.LastOk == true && _state.LastAttemptUtc is { } last && now - last < RecentSuccess)
        {
            return new DhanAutoSignInResult(true,
                $"Already signed in {(now - last).TotalSeconds:0} s ago ({_state.LastTrigger}); Dhan was not asked again. {_state.LastMessage}",
                _state.LastExpiresUtc, null, Tried: false);
        }

        string? why = await WhyNotAutomaticallyAsync(now, cancellationToken);
        return why is null ? null : new DhanAutoSignInResult(false, $"Not tried: {why}.", null, null, Tried: false);
    }

    /// <summary>
    /// Why a machine may not try now: this process's own stop or pause, else a
    /// stop saved earlier today, which outlives a restart.
    /// </summary>
    /// <remarks>
    /// A read that fails holds the machine back. Skipping costs a morning of
    /// pressing Connect; trying could send a PIN Dhan refused an hour ago and
    /// lock the account.
    /// </remarks>
    private async Task<string?> WhyNotAutomaticallyAsync(DateTime now, CancellationToken cancellationToken)
    {
        if (!_state.MayTryAutomatically(now, out string why)) return why;

        DhanAutoSignInStop? stored;
        try
        {
            stored = await ReadStopAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not read whether the Dhan automatic sign-in was stopped earlier today; not trying (a PIN Dhan refused, sent again, can lock the account). Connect still works.");
            return "could not read whether it was stopped earlier today";
        }

        if (stored is not null && _state.Restore(stored, now) && !_state.MayTryAutomatically(now, out why)) return why;
        return null;
    }

    private async Task<DhanAutoSignInStop?> ReadStopAsync(CancellationToken cancellationToken)
    {
        string? raw = await _settings.GetAsync(SystemSettingKeys.DhanAutoSignInStopped, cancellationToken);
        var stop = DhanAutoSignInStop.FromStored(raw);
        if (stop is null && !string.IsNullOrWhiteSpace(raw))
            _logger.LogWarning("Ignoring {Key}: \"{Value}\" is not \"yyyy-MM-dd: reason\".", SystemSettingKeys.DhanAutoSignInStopped, raw);
        return stop;
    }

    private async Task<DhanAutoSignInResult> FailedAsync(string trigger, string message, DhanSignInFailure failure, CancellationToken cancellationToken)
    {
        var stop = _state.Failed(Now, trigger, message, failure);
        _logger.LogWarning("Dhan automatic sign-in ({Trigger}) failed: {Message}", trigger, message);

        bool saved = stop is null || await SaveStopAsync(stop, trigger);
        string next = stop is not null
            ? " No more automatic tries today (a wrong PIN, repeated, can lock the account)."
            : $" It will be tried again in {DhanAutoSignInState.RetryAfter.TotalMinutes:0} minutes.";
        string unsaved = saved ? string.Empty : " The stop could not be saved: an API restart today would try again.";
        await _notifier.NotifyAsync(NotificationCategory.Connector, NotificationSeverity.Error,
            "Dhan did not sign in by itself",
            $"{message}{(trigger == ConsoleTrigger ? string.Empty : next)}{unsaved} Press Connect on Connectors > Dhan.",
            cancellationToken: cancellationToken);
        return new DhanAutoSignInResult(false, message, null, failure);
    }

    private async Task<bool> SaveStopAsync(DhanAutoSignInStop stop, string trigger)
    {
        try
        {
            // Not the caller's token: the stop has happened, and a request that
            // hangs up must not leave the database without it.
            await _settings.SetAsync(SystemSettingKeys.DhanAutoSignInStopped, stop.Stored, trigger, CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "The Dhan automatic sign-in stopped for today ({Reason}), but that could not be saved; an API restart today would try again.", stop.Reason);
            return false;
        }
    }

    /// <summary>A sign-in that worked ends any stop saved today: it is how "Sign in now" after fixing the PIN turns the machine back on.</summary>
    private async Task ClearStopAsync()
    {
        try
        {
            await _settings.DeleteAsync(SystemSettingKeys.DhanAutoSignInStopped, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The safe way round: the machine stays off until tomorrow.
            _logger.LogWarning(ex, "Signed in to Dhan, but the day's saved stop could not be cleared; after an API restart the automatic sign-in stays off until tomorrow.");
        }
    }
}

/// <summary>What one call to <see cref="DhanAutoSignInService.SignInAsync"/> did.</summary>
/// <param name="Tried">
/// False when Dhan was not asked: a machine held back (stopped for the day, or
/// pausing after a failure), or answered with a sign-in made moments earlier.
/// </param>
public sealed record DhanAutoSignInResult(bool Ok, string Message, DateTime? ExpiresUtc, DhanSignInFailure? Failure, bool Tried = true);

/// <summary>
/// Checks once a minute whether Dhan needs a new token (<see cref="DhanAutoSignInPolicy"/>)
/// and takes one. Does nothing at all unless the PIN and TOTP secret are set.
/// </summary>
/// <remarks>
/// Before this, the day's Dhan token came only from a person pressing Connect.
/// On 27 Sep both Dhan and FYERS were signed out on a Sunday night, and the
/// Monday morning depended on someone remembering before 08:45.
/// </remarks>
public sealed class DhanAutoSignInWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<DhanSettings> _settings;
    private readonly DhanAutoSignInState _state;
    private readonly ILogger<DhanAutoSignInWorker> _logger;
    private readonly TimeProvider _time;

    public DhanAutoSignInWorker(
        IServiceScopeFactory scopes,
        IOptionsMonitor<DhanSettings> settings,
        DhanAutoSignInState state,
        ILogger<DhanAutoSignInWorker> logger,
        TimeProvider? time = null)
    {
        _scopes = scopes;
        _settings = settings;
        _state = state;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RestoreAsync(stoppingToken);

            // Let the API finish starting: the morning job's own check comes
            // right after a restart, and this should not race it for a code.
            await Task.Delay(StartDelay, _time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Dhan automatic sign-in check failed; trying again in a minute.");
                }
                await Task.Delay(Interval, _time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>At startup: a stop saved earlier today, so the connector page shows it straight away.</summary>
    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        if (!settings.AutoSignIn.Enabled || !settings.HasAutoSignInSecrets) return;

        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DhanAutoSignInService>().RestoreStopAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never stops the API from starting; every automatic try reads the stop itself.
            _logger.LogWarning(ex, "Could not restore the Dhan automatic sign-in's stop at startup.");
        }
    }

    /// <summary>One check: sign in if the policy says so and the day's tries allow it.</summary>
    public async Task<DhanAutoSignInResult?> CheckOnceAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        if (!settings.AutoSignIn.Enabled || !settings.HasAutoSignInSecrets) return null;

        using var scope = _scopes.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IBrokerSessionStore>();
        var session = await sessions.GetForProviderAsync(DhanProvider.Key, cancellationToken);

        var now = _time.GetUtcNow().UtcDateTime;
        DateTime? validUntil = session is not null && session.IsAuthenticatedAt(now) ? session.ExpiresAtUtc : null;
        string? reason = DhanAutoSignInPolicy.ReasonToSignIn(now, validUntil, settings.AutoSignIn);
        if (reason is null) return null;

        // The cheap look, in memory. The service looks again inside its gate,
        // including at a stop saved before a restart.
        if (!_state.MayTryAutomatically(now, out string why))
        {
            _logger.LogDebug("Dhan needs a sign-in ({Reason}) but {Why}.", reason, why);
            return null;
        }

        var service = scope.ServiceProvider.GetRequiredService<DhanAutoSignInService>();
        return await service.SignInAsync("automatic", reason, cancellationToken);
    }
}
