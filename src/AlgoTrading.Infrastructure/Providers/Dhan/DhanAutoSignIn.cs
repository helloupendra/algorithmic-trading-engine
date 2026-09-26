using AlgoTrading.Application.Interfaces;
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
/// The rule is "only when needed", never "every so often". Dhan's documentation
/// says <c>RenewToken</c> expires the token it renews, and says nothing either
/// way about whether a fresh PIN + TOTP sign-in ends the previous token. A
/// sign-in in the middle of the session could therefore cut off a feed that was
/// streaming happily. So a new token is taken only
/// </para>
/// <list type="bullet">
/// <item>when there is no sign-in that is still valid — nothing is working on
/// it, so nothing can be cut off; or</item>
/// <item>in the morning window before the 08:45 job starts the feeds, when the
/// token would end before tonight's MCX close. A Connect pressed at 13:00 gives a
/// token that dies at 13:00 the next day, in the middle of the session; this is
/// what replaces it before anything depends on it.</item>
/// </list>
/// <para>
/// Weekdays only (IST). The feeds do not run at the weekend, and a token taken
/// on Saturday would be dead by Monday anyway. The morning job asks for a
/// sign-in itself when it finds none, so a special session is still covered.
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

        if (validUntilUtc is not { } until)
            return "Dhan had no valid sign-in";
        if (until - utcNow < EndingMargin)
            return $"the token was ending at {Ist(until)} IST";

        var time = ist.TimeOfDay;
        if (time >= settings.MorningFromIst && time < settings.MorningUntilIst)
        {
            var endOfDayUtc = ist.Date.AddHours(23).AddMinutes(59) - IstOffset;
            if (until < endOfDayUtc)
                return $"the token would end at {Ist(until)} IST, before tonight's session closes";
        }

        return null;
    }

    private static string Ist(DateTime utc) => (utc + IstOffset).ToString("HH:mm");
}

/// <summary>What the automatic sign-in last did, shared by the worker, the endpoint and the connector page.</summary>
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
    private bool _stoppedToday;

    /// <summary>One sign-in at a time: the worker and a button pressed at the same moment would spend two codes.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public DateTime? LastAttemptUtc { get; private set; }
    public bool? LastOk { get; private set; }
    public string? LastMessage { get; private set; }

    /// <summary>"automatic", "morning job" or "console".</summary>
    public string? LastTrigger { get; private set; }

    public DateTime? LastExpiresUtc { get; private set; }

    /// <summary>True once Dhan has refused today, or the tries are used up: no more automatic tries until tomorrow.</summary>
    public bool StoppedFor(DateTime utcNow)
    {
        lock (_lock)
        {
            Roll(utcNow);
            return _stoppedToday;
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
            if (_stoppedToday)
            {
                why = "stopped for today: Dhan refused, a value is missing, or three tries failed";
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
            _stoppedToday = false;
        }
    }

    /// <returns>True when this failure used up the day: say so once, loudly.</returns>
    public bool Failed(DateTime utcNow, string trigger, string message, DhanSignInFailure failure)
    {
        lock (_lock)
        {
            Roll(utcNow);
            LastAttemptUtc = utcNow;
            LastOk = false;
            LastTrigger = trigger;
            LastMessage = message;
            _triesToday++;
            bool wasStopped = _stoppedToday;
            if (failure != DhanSignInFailure.Unreachable || _triesToday >= MaxTriesPerDay) _stoppedToday = true;
            return _stoppedToday && !wasStopped;
        }
    }

    private void Roll(DateTime utcNow)
    {
        var today = DateOnly.FromDateTime(utcNow + IstOffset);
        if (today == _day) return;
        _day = today;
        _triesToday = 0;
        _stoppedToday = false;
    }
}

/// <summary>
/// Takes the token, records what happened and says so on Telegram. Shared by
/// the background worker, the morning job's call and the console's button, so
/// all three are one code path.
/// </summary>
public sealed class DhanAutoSignInService
{
    private readonly DhanLoginFlow _login;
    private readonly DhanAutoSignInState _state;
    private readonly ISystemNotifier _notifier;
    private readonly ILogger<DhanAutoSignInService> _logger;
    private readonly TimeProvider _time;

    public DhanAutoSignInService(
        DhanLoginFlow login,
        DhanAutoSignInState state,
        ISystemNotifier notifier,
        ILogger<DhanAutoSignInService> logger,
        TimeProvider? time = null)
    {
        _login = login;
        _state = state;
        _notifier = notifier;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// One sign-in. Never throws for a failed sign-in: the result says what
    /// happened, and the state and Telegram have both been told.
    /// </summary>
    public async Task<DhanAutoSignInResult> SignInAsync(string trigger, string reason, CancellationToken cancellationToken)
    {
        await _state.Gate.WaitAsync(cancellationToken);
        try
        {
            DhanSignIn signIn;
            try
            {
                signIn = await _login.SignInWithTotpAsync(cancellationToken);
            }
            catch (DhanSignInException ex)
            {
                var now = _time.GetUtcNow().UtcDateTime;
                bool dayOver = _state.Failed(now, trigger, ex.Message, ex.Failure);
                _logger.LogWarning("Dhan automatic sign-in ({Trigger}) failed: {Message}", trigger, ex.Message);
                string next = dayOver
                    ? " No more automatic tries today (a wrong PIN, repeated, can lock the account)."
                    : $" It will be tried again in {DhanAutoSignInState.RetryAfter.TotalMinutes:0} minutes.";
                await _notifier.NotifyAsync(NotificationCategory.Connector, NotificationSeverity.Error,
                    "Dhan did not sign in by itself",
                    $"{ex.Message}{(trigger == "console" ? string.Empty : next)} Press Connect on Connectors > Dhan.",
                    cancellationToken: cancellationToken);
                return new DhanAutoSignInResult(false, ex.Message, null, ex.Failure);
            }

            string until = (signIn.ExpiresUtc + TimeSpan.FromMinutes(330)).ToString("ddd HH:mm");
            string message = $"Signed in with the PIN and a TOTP code ({reason}); token valid until {until} IST.";
            _state.Succeeded(_time.GetUtcNow().UtcDateTime, trigger, message, signIn.ExpiresUtc);
            await _notifier.NotifyAsync(NotificationCategory.Connector, NotificationSeverity.Success,
                "Dhan signed in by itself", message, cancellationToken: cancellationToken);
            return new DhanAutoSignInResult(true, message, signIn.ExpiresUtc, null);
        }
        finally
        {
            _state.Gate.Release();
        }
    }
}

public sealed record DhanAutoSignInResult(bool Ok, string Message, DateTime? ExpiresUtc, DhanSignInFailure? Failure);

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

        if (!_state.MayTryAutomatically(now, out string why))
        {
            _logger.LogDebug("Dhan needs a sign-in ({Reason}) but {Why}.", reason, why);
            return null;
        }

        var service = scope.ServiceProvider.GetRequiredService<DhanAutoSignInService>();
        return await service.SignInAsync("automatic", reason, cancellationToken);
    }
}
