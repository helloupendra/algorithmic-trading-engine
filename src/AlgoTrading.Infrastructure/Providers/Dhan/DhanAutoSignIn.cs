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
/// (08:00). Nothing streams on Dhan before the 08:45 job, and a token already
/// dead at midnight was once tried for at 00:00:30, which could spend the day's
/// tries before the morning.
/// </para>
/// <para>
/// Never while the last token is still valid, nor within <see cref="AfterTokenEnds"/>
/// of its end: the earliest try is the later of 08:00 and that. On 30 Sep the
/// token taken at 08:00:02 the day before ended at 08:00:02; the sign-in went at
/// 08:00:11, Dhan answered "Invalid TOTP" to a code from the secret that had
/// worked the day before, and the refusal stopped the day until the owner
/// pressed Connect at 08:02. The likeliest reading is that Dhan will not issue a
/// token while it still counts the last one as live, and its clock and ours
/// need not agree to the second. Its documentation says <c>RenewToken</c> ends
/// the token it renews and nothing either way about a fresh PIN + TOTP sign-in.
/// </para>
/// <para>
/// That also ends two older rules. A token was replaced ten minutes before its
/// end, and between 08:00 and <see cref="DhanAutoSignInSettings.MorningUntilIst"/>
/// whenever it would end before tonight's close; both sign in while a token is
/// live, which is what Dhan refused. A token that ends in the session (a Connect
/// pressed at 13:00 ends at 13:00 the next day) is now replaced two minutes after
/// it ends, and the 08:45 job still asks for Connect when the token will not
/// last to the MCX close.
/// </para>
/// <para>
/// Waiting two minutes past the last token moves the sign-in about two minutes
/// later each day (08:00, 08:02, 08:04 …). A weekend, when nothing signs in,
/// puts Monday back at 08:00, and <see cref="NoTokenAlertUtc"/> allows for it.
/// </para>
/// </remarks>
public static class DhanAutoSignInPolicy
{
    private static readonly TimeSpan IstOffset = TimeSpan.FromMinutes(330);

    /// <summary>How long after the last token ends before a new one is asked for.</summary>
    public static readonly TimeSpan AfterTokenEnds = TimeSpan.FromMinutes(2);

    /// <summary>Codes one sign-in may send when Dhan refuses the code (not the PIN): the first and two retries.</summary>
    public const int MaxCodeTries = 3;

    /// <summary>
    /// Time for a sign-in that could only start late to finish before
    /// <see cref="NoTokenAlertUtc"/> calls it failed: the worker's one-minute
    /// look and three codes about half a minute apart.
    /// </summary>
    public static readonly TimeSpan AlertGrace = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The earliest an automatic sign-in may go on <paramref name="utcNow"/>'s IST
    /// day: the later of the morning start and <see cref="AfterTokenEnds"/> past
    /// the last token's end. <paramref name="lastTokenEndsUtc"/> is when the last
    /// token on record ends or ended, valid or not; null when there is none.
    /// </summary>
    public static DateTime EarliestAttemptUtc(DateTime utcNow, DateTime? lastTokenEndsUtc, DhanAutoSignInSettings settings)
    {
        var morningUtc = (utcNow + IstOffset).Date + settings.MorningFromIst - IstOffset;
        return lastTokenEndsUtc is { } ends && ends + AfterTokenEnds > morningUtc ? ends + AfterTokenEnds : morningUtc;
    }

    /// <summary>
    /// Why a sign-in is needed now, or null when it is not.
    /// <paramref name="lastTokenEndsUtc"/> is when the last token on record ends
    /// or ended, or null when there is none.
    /// </summary>
    public static string? ReasonToSignIn(DateTime utcNow, DateTime? lastTokenEndsUtc, DhanAutoSignInSettings settings)
    {
        var ist = utcNow + IstOffset;
        if (ist.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return null;
        if (utcNow < EarliestAttemptUtc(utcNow, lastTokenEndsUtc, settings)) return null;

        return lastTokenEndsUtc is { } ends
            ? $"the last token ended at {Ist(ends)} IST"
            : "Dhan had no sign-in on record";
    }

    /// <summary>
    /// Why a machine must not ask Dhan yet because of the last token, or null
    /// when it may. The rule of <see cref="EarliestAttemptUtc"/> without the
    /// morning start, for the morning job, which runs after it.
    /// </summary>
    public static string? WhyNotYet(DateTime utcNow, DateTime? lastTokenEndsUtc)
    {
        if (lastTokenEndsUtc is not { } ends || utcNow >= ends + AfterTokenEnds) return null;
        string state = utcNow < ends ? $"is valid until {Ist(ends)} IST" : $"ended only at {Ist(ends)} IST";
        return $"the last Dhan token {state}, and Dhan has refused a new one that soon (30 Sep); " +
               $"the automatic sign-in tries from {Ist(ends + AfterTokenEnds)} IST";
    }

    /// <summary>
    /// When to say loudly that the day has no token: <see cref="DhanAutoSignInSettings.NoTokenAlertIst"/>
    /// (08:10), or later when the last token let the sign-in start only late, but
    /// never after <see cref="DhanAutoSignInSettings.MorningUntilIst"/> (08:40),
    /// which leaves a person five minutes to press Connect before the 08:45 job.
    /// </summary>
    /// <remarks>
    /// The later start is what the daily drift (see the class remarks) makes
    /// routine by Thursday: a token that ended at 08:07 is replaced from 08:09,
    /// and an alert at 08:10 sharp would call that sign-in failed while it runs.
    /// </remarks>
    public static DateTime NoTokenAlertUtc(DateTime utcNow, DateTime? lastTokenEndsUtc, DhanAutoSignInSettings settings)
    {
        var day = (utcNow + IstOffset).Date;
        var alertUtc = day + settings.NoTokenAlertIst - IstOffset;
        var latestUtc = day + settings.MorningUntilIst - IstOffset;
        var afterTries = EarliestAttemptUtc(utcNow, lastTokenEndsUtc, settings) + AlertGrace;
        var at = afterTries < latestUtc ? afterTries : latestUtc;
        return at > alertUtc ? at : alertUtc;
    }

    /// <summary>
    /// Whether one sign-in sends another code after a refusal. Only the code is
    /// worth it, and only with a fresh one; a PIN refusal never is, and wording
    /// not recognised gets one more try.
    /// </summary>
    public static bool TryAnotherCode(DhanRefusal? refusal, int codesSent) => refusal switch
    {
        DhanRefusal.Code => codesSent < MaxCodeTries,
        DhanRefusal.Unrecognised => codesSent < 2,
        _ => false,
    };

    private static string Ist(DateTime utc) => (utc + IstOffset).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
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
    /// <summary>
    /// Tries per IST day when Dhan cannot be reached. A refusal that one sign-in
    /// could not get past (<see cref="DhanAutoSignInPolicy.TryAnotherCode"/>), or
    /// a missing value, stops the day at once.
    /// </summary>
    public const int MaxTriesPerDay = 3;

    /// <summary>The gap after an unreachable Dhan before the machine tries again.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan IstOffset = TimeSpan.FromMinutes(330);
    private readonly object _lock = new();
    private DateOnly _day;
    private int _triesToday;
    private string? _stoppedBecause;
    private long? _lastCodeStep;
    private DateOnly? _noTokenAlertDay;

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
    public DhanAutoSignInStop? Failed(DateTime utcNow, string trigger, string message, DhanSignInFailure failure, DhanRefusal? refusal = null)
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
                DhanSignInFailure.Refused => refusal switch
                {
                    DhanRefusal.Pin => "Dhan refused the PIN",
                    DhanRefusal.Code => $"Dhan refused {DhanAutoSignInPolicy.MaxCodeTries} TOTP codes in a row",
                    DhanRefusal.Account => "Dhan signed in to another account",
                    _ => "Dhan refused the PIN or the code",
                },
                DhanSignInFailure.NotSetUp => "a value it needs is missing or malformed",
                _ when _triesToday >= MaxTriesPerDay => $"{MaxTriesPerDay} tries failed",
                _ => null,
            };
            return _stoppedBecause is null ? null : new DhanAutoSignInStop(_day, _stoppedBecause);
        }
    }

    /// <summary>
    /// True the first time it is asked on an IST day: the no-token alert goes
    /// once a day. In memory, so an API restart after the alert may send it once
    /// more, which for a day with no token is the right side to err on.
    /// </summary>
    public bool ClaimNoTokenAlert(DateTime utcNow)
    {
        lock (_lock)
        {
            Roll(utcNow);
            if (_noTokenAlertDay == _day) return false;
            _noTokenAlertDay = _day;
            return true;
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
    private readonly IBrokerSessionStore _sessions;
    private readonly ISystemNotifier _notifier;
    private readonly ILogger<DhanAutoSignInService> _logger;
    private readonly TimeProvider _time;

    public DhanAutoSignInService(
        DhanLoginFlow login,
        DhanAutoSignInState state,
        IProcessSettingsStore settings,
        IBrokerSessionStore sessions,
        ISystemNotifier notifier,
        ILogger<DhanAutoSignInService> logger,
        TimeProvider? time = null)
    {
        _login = login;
        _state = state;
        _settings = settings;
        _sessions = sessions;
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
    /// <remarks>
    /// When Dhan refuses the code (not the PIN), the same sign-in sends up to two
    /// more, each read one second into a TOTP step no code has come from
    /// (<see cref="DhanLoginFlow.WaitBeforeCode"/>), so all three go within about
    /// a minute. Not wider: the console's request comes through the Cloudflare
    /// tunnel, which gives up after 100 seconds.
    /// </remarks>
    public async Task<DhanAutoSignInResult> SignInAsync(string trigger, string reason, CancellationToken cancellationToken)
    {
        await _state.Gate.WaitAsync(cancellationToken);
        try
        {
            // Decided again inside the gate. The worker and the morning job each
            // decided before waiting for it, so the second in could send a code
            // after the first had just signed in, or just been refused.
            LastToken last;
            if (trigger != ConsoleTrigger)
            {
                if (await HoldBackAsync(cancellationToken) is { } held) return held;
                last = await LastTokenAsync(cancellationToken);
                string? notYet = last.Known
                    ? DhanAutoSignInPolicy.WhyNotYet(Now, last.EndsUtc)
                    : "the current Dhan session could not be read, so it is not known whether the last token has ended";
                if (notYet is not null) return new DhanAutoSignInResult(false, $"Not tried: {notYet}.", null, null, Tried: false);
            }
            else
            {
                // A person is never held back; the last token is read for the log only.
                last = await LastTokenAsync(cancellationToken);
            }

            DhanSignIn signIn;
            var refused = new List<string>();
            for (int codes = 1; ; codes++)
            {
                DateTimeOffset? codeReadAt = null;
                try
                {
                    signIn = await _login.SignInWithTotpAsync(_state.LastCodeStep, step =>
                    {
                        codeReadAt = _time.GetUtcNow();
                        _state.CodeSent(step);
                    }, cancellationToken);
                    LogTry(trigger, codes, codeReadAt, last, ok: true, "a token");
                    break;
                }
                catch (DhanSignInException ex)
                {
                    LogTry(trigger, codes, codeReadAt, last, ok: false,
                        ex.Failure == DhanSignInFailure.Refused
                            ? $"refused ({ex.Refusal}): {(ex.DhanReason.Length > 0 ? ex.DhanReason : "no reason given")}"
                            : ex.Message);

                    // codeReadAt is null when no code went (not set up): nothing to retry.
                    if (codeReadAt is not null && ex.Failure == DhanSignInFailure.Refused
                        && DhanAutoSignInPolicy.TryAnotherCode(ex.Refusal, codes))
                    {
                        refused.Add(ex.DhanReason.Length > 0 ? ex.DhanReason : "no reason given");
                        continue;
                    }

                    string failed = codes > 1 ? $"{ex.Message} {codes} codes were tried, each from a new TOTP step." : ex.Message;
                    return await FailedAsync(trigger, failed, ex.Failure, cancellationToken, ex.Refusal);
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
            }

            string until = (signIn.ExpiresUtc + IstOffset).ToString("ddd HH:mm", CultureInfo.InvariantCulture);
            string retried = refused.Count == 0
                ? string.Empty
                : $" Dhan refused {refused.Count} code{(refused.Count == 1 ? string.Empty : "s")} first ({string.Join("; ", refused.Distinct())}); a fresh one worked.";
            string message = $"Signed in with the PIN and a TOTP code ({reason}); token valid until {until} IST.{retried}";
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

    /// <summary>When the last Dhan token on record ends or ended, valid or not; not <see cref="LastToken.Known"/> when it could not be read.</summary>
    private async Task<LastToken> LastTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var session = await _sessions.GetForProviderAsync(DhanProvider.Key, cancellationToken);
            return new LastToken(true, session is not null && !string.IsNullOrWhiteSpace(session.AccessToken) ? session.ExpiresAtUtc : null);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Could not read the current Dhan session before signing in.");
            return new LastToken(false, null);
        }
    }

    /// <summary>
    /// One line per code sent, for the next morning that goes wrong: when (IST),
    /// how far into its 30-second TOTP step the code was read, how the last
    /// token stood, and what Dhan said. Never the PIN, the secret or the code:
    /// Dhan's reason arrives with them taken out (<see cref="DhanSignInException.DhanReason"/>).
    /// </summary>
    /// <remarks>
    /// 28 and 30 Sep were each one line, "Invalid TOTP", with nothing to say
    /// whether the code came late in its step or the last token was still live.
    /// </remarks>
    private void LogTry(string trigger, int codes, DateTimeOffset? codeReadAt, LastToken last, bool ok, string answer)
    {
        var at = codeReadAt ?? _time.GetUtcNow();
        string ist = (at.UtcDateTime + IstOffset).ToString("HH:mm:ss.f", CultureInfo.InvariantCulture);
        string previous = last.Describe(at.UtcDateTime);
        if (codeReadAt is null)
        {
            _logger.LogWarning("Dhan sign-in ({Trigger}) at {Ist} IST sent no code; {Previous}. {Answer}", trigger, ist, previous, answer);
            return;
        }

        double intoStep = (at - DateTimeOffset.FromUnixTimeSeconds(DhanLoginFlow.CodeStep(at) * 30)).TotalSeconds;
        _logger.Log(ok ? LogLevel.Information : LogLevel.Warning,
            "Dhan sign-in ({Trigger}) code {Code} of at most {Max} read at {Ist} IST, {IntoStep:0.0} s into its TOTP step; {Previous}. Dhan answered: {Answer}",
            trigger, codes, DhanAutoSignInPolicy.MaxCodeTries, ist, intoStep, previous, answer);
    }

    /// <summary>The last Dhan token on record, as far as the session store knows.</summary>
    private readonly record struct LastToken(bool Known, DateTime? EndsUtc)
    {
        public string Describe(DateTime utcNow)
        {
            if (!Known) return "the last token could not be read";
            if (EndsUtc is not { } ends) return "no earlier token on record";
            string when = (ends + IstOffset).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            return ends > utcNow
                ? $"the last token was still valid for {Span(ends - utcNow)} (until {when} IST)"
                : $"the last token had ended {Span(utcNow - ends)} earlier (at {when} IST)";
        }

        private static string Span(TimeSpan span) =>
            span.TotalMinutes < 1 ? $"{span.TotalSeconds:0} s"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min {span.Seconds} s"
            : $"{(int)span.TotalHours} h {span.Minutes} min";
    }

    private async Task<DhanAutoSignInResult> FailedAsync(string trigger, string message, DhanSignInFailure failure, CancellationToken cancellationToken, DhanRefusal? refusal = null)
    {
        var stop = _state.Failed(Now, trigger, message, failure, refusal);
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
                await Task.Delay(DelayBeforeNextCheck(_time.GetUtcNow().UtcDateTime, _earliestAttemptUtc), _time, stoppingToken);
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

    /// <summary>
    /// The wait before the next look: a minute, or less when the earliest try
    /// falls inside that minute, so the sign-in goes at that moment (plus a
    /// second) rather than up to a minute after it. Every minute lost there is
    /// carried into the next day's start (see <see cref="DhanAutoSignInPolicy"/>).
    /// </summary>
    public static TimeSpan DelayBeforeNextCheck(DateTime utcNow, DateTime? earliestAttemptUtc) =>
        earliestAttemptUtc is { } at && at > utcNow && at - utcNow < Interval
            ? at - utcNow + TimeSpan.FromSeconds(1)
            : Interval;

    private DateTime? _earliestAttemptUtc;

    /// <summary>
    /// One check: sign in if the policy says so and the day's tries allow it,
    /// then raise the alarm once if the morning has passed with no token.
    /// </summary>
    public async Task<DhanAutoSignInResult?> CheckOnceAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.CurrentValue;
        if (!settings.AutoSignIn.Enabled || !settings.HasAutoSignInSecrets) return null;

        using var scope = _scopes.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IBrokerSessionStore>();
        var session = await sessions.GetForProviderAsync(DhanProvider.Key, cancellationToken);

        var now = _time.GetUtcNow().UtcDateTime;
        // The last token's end whether or not it is still valid: the next try
        // waits for it (30 Sep, DhanAutoSignInPolicy).
        DateTime? lastEnds = session is not null && !string.IsNullOrWhiteSpace(session.AccessToken) ? session.ExpiresAtUtc : null;
        var earliest = DhanAutoSignInPolicy.EarliestAttemptUtc(now, lastEnds, settings.AutoSignIn);
        _earliestAttemptUtc = earliest > now ? earliest : null;

        DhanAutoSignInResult? result = null;
        string? reason = DhanAutoSignInPolicy.ReasonToSignIn(now, lastEnds, settings.AutoSignIn);
        if (reason is not null)
        {
            // The cheap look, in memory. The service looks again inside its gate,
            // including at a stop saved before a restart.
            if (_state.MayTryAutomatically(now, out string why))
            {
                var service = scope.ServiceProvider.GetRequiredService<DhanAutoSignInService>();
                result = await service.SignInAsync("automatic", reason, cancellationToken);
            }
            else
            {
                _logger.LogDebug("Dhan needs a sign-in ({Reason}) but {Why}.", reason, why);
            }
        }

        bool signedIn = result?.Ok == true || (session is not null && session.IsAuthenticatedAt(_time.GetUtcNow().UtcDateTime));
        if (!signedIn) await AlertIfNoTokenAsync(scope.ServiceProvider, sessions, lastEnds, settings.AutoSignIn, cancellationToken);
        return result;
    }

    /// <summary>
    /// One loud message on a trading day that reaches <see cref="DhanAutoSignInPolicy.NoTokenAlertUtc"/>
    /// (08:10) with no valid Dhan token, whatever the reason: refused, stopped,
    /// not reached, or never tried.
    /// </summary>
    /// <remarks>
    /// On 30 Sep the refusal at 08:00:11 stopped the day with one Telegram line
    /// among the morning's others, and on 28 Sep the day stopped the same way.
    /// Whatever went wrong, a morning with no token by 08:10 gets its own
    /// message; the feeds start at 08:45, which leaves half an hour to press Connect.
    /// </remarks>
    private async Task AlertIfNoTokenAsync(IServiceProvider services, IBrokerSessionStore sessions, DateTime? lastEnds,
        DhanAutoSignInSettings settings, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (now < DhanAutoSignInPolicy.NoTokenAlertUtc(now, lastEnds, settings) || !IsTradingDay(services, now)) return;

        // Read again before saying so: a person may have pressed Connect while
        // this check was signing in, and a false alarm teaches people to ignore it.
        var session = await sessions.GetForProviderAsync(DhanProvider.Key, cancellationToken);
        if (session is not null && session.IsAuthenticatedAt(now)) return;
        if (!_state.ClaimNoTokenAlert(now)) return;

        var ist = now + TimeSpan.FromMinutes(330);
        string press = ist.TimeOfDay < new TimeSpan(8, 45, 0)
            ? "Press Connect on Connectors > Dhan before 08:45, when the morning job starts the feeds."
            : "Press Connect on Connectors > Dhan now: the 08:45 morning job has already looked for it.";
        string last = _state.LastMessage is { Length: > 0 } message
            ? $" Last try: {message}"
            : " It has not tried today.";
        _logger.LogWarning("No valid Dhan token at {Time} IST; the automatic sign-in did not get one.", ist.ToString("HH:mm", CultureInfo.InvariantCulture));
        await services.GetRequiredService<ISystemNotifier>().NotifyAsync(NotificationCategory.Connector, NotificationSeverity.Error,
            "Dhan automatic sign-in failed: press Connect",
            $"No valid Dhan token at {ist.ToString("HH:mm", CultureInfo.InvariantCulture)} IST.{last} {press}",
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// An NSE trading day by the holiday calendar, as the forecast scheduler
    /// reads it; weekdays when the calendar service is not registered.
    /// </summary>
    private static bool IsTradingDay(IServiceProvider services, DateTime utcNow)
    {
        var ist = utcNow + TimeSpan.FromMinutes(330);
        if (ist.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        var market = services.GetService<IMarketSessionService>();
        return market is null || market.GetSessionInfo(utcNow, "NSE", "CM").IsTradingDay;
    }
}
