using System.Net;
using System.Security.Cryptography;
using System.Text;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Providers;
using AlgoTrading.Infrastructure.Providers.Dhan;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Dhan's PIN + TOTP sign-in, and the rule that decides when the desk takes a
/// token by itself. The rule matters more than the call: a sign-in at the wrong
/// moment can cut off a feed that was streaming, and one retried after a refusal
/// can lock the account.
/// </summary>
public class DhanAutoSignInTests
{
    private const string ClientId = "1113706926";
    private const string Pin = "482913";
    // RFC 6238's SHA-1 test key, "12345678901234567890", in base32.
    private const string Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    // Tuesday 29 Sep 2026, 08:05:10 IST: inside the morning window, early in a TOTP step.
    private static readonly DateTimeOffset TuesdayMorning = new(2026, 9, 29, 2, 35, 10, TimeSpan.Zero);

    // ------------------------------------------------------------ the call

    [Fact]
    public async Task Sends_the_client_id_PIN_and_the_current_code_and_saves_the_session()
    {
        var handler = new Handler($$"""{"dhanClientId":"{{ClientId}}","dhanClientName":"X","accessToken":"jwt-totp","expiryTime":"2026-09-30T08:05:10"}""");
        var (flow, sessions) = Build(handler);

        var signIn = await flow.SignInWithTotpAsync();

        string code = Totp.Generate(Secret, TuesdayMorning);
        Assert.Equal($"https://auth.dhan.co/app/generateAccessToken?dhanClientId={ClientId}&pin={Pin}&totp={code}", handler.LastUrl);
        Assert.Equal("POST", handler.LastMethod);
        var saved = Assert.Single(sessions.Saved);
        Assert.Equal("dhan", saved.ProviderKey);
        Assert.Equal("jwt-totp", saved.AccessToken);
        Assert.Equal(TuesdayMorning.UtcDateTime.AddHours(24), signIn.ExpiresUtc);
    }

    [Fact]
    public async Task A_refusal_is_marked_refused_and_never_repeats_the_PIN()
    {
        var handler = new Handler("""{"errorCode":"DH-906","errorMessage":"Invalid TOTP","pin":"482913"}""", HttpStatusCode.BadRequest);
        var (flow, sessions) = Build(handler);

        var ex = await Assert.ThrowsAsync<DhanSignInException>(() => flow.SignInWithTotpAsync());

        Assert.Equal(DhanSignInFailure.Refused, ex.Failure);
        Assert.Equal(DhanRefusal.Code, ex.Refusal);
        Assert.Equal("DH-906 — Invalid TOTP", ex.DhanReason);
        Assert.Contains("Invalid TOTP", ex.Message);
        Assert.DoesNotContain(Pin, ex.Message);
        Assert.Empty(sessions.Saved);
    }

    [Fact]
    public async Task A_reason_that_echoes_the_PIN_or_the_code_is_scrubbed()
    {
        // Dhan has not been seen to do it; the reason goes to the log and Telegram.
        string code = Totp.Generate(Secret, TuesdayMorning);
        var (flow, _) = Build(new Handler($$"""{"errorMessage":"TOTP {{code}} rejected for {{Pin}}"}"""));

        var ex = await Assert.ThrowsAsync<DhanSignInException>(() => flow.SignInWithTotpAsync());

        Assert.DoesNotContain(Pin, ex.Message + ex.DhanReason);
        Assert.DoesNotContain(code, ex.Message + ex.DhanReason);
        Assert.Contains("TOTP … rejected", ex.DhanReason);
    }

    [Fact]
    public async Task A_200_without_a_token_is_a_refusal_not_a_sign_in()
    {
        var (flow, sessions) = Build(new Handler("""{"status":"failure","message":"Invalid pin"}"""));

        var ex = await Assert.ThrowsAsync<DhanSignInException>(() => flow.SignInWithTotpAsync());

        Assert.Equal(DhanSignInFailure.Refused, ex.Failure);
        Assert.Empty(sessions.Saved);
    }

    [Fact]
    public async Task A_server_error_is_worth_another_try()
    {
        var (flow, _) = Build(new Handler("<html>bad gateway</html>", HttpStatusCode.BadGateway));

        var ex = await Assert.ThrowsAsync<DhanSignInException>(() => flow.SignInWithTotpAsync());

        Assert.Equal(DhanSignInFailure.Unreachable, ex.Failure);
    }

    [Fact]
    public async Task Names_what_is_missing_without_calling_Dhan()
    {
        var handler = new Handler("{}");
        var (flow, _) = Build(handler, pin: "");

        var ex = await Assert.ThrowsAsync<DhanSignInException>(() => flow.SignInWithTotpAsync());

        Assert.Equal(DhanSignInFailure.NotSetUp, ex.Failure);
        Assert.Contains("DHAN_PIN", ex.Message);
        Assert.DoesNotContain("DHAN_TOTP_SECRET", ex.Message);
        Assert.Equal(string.Empty, handler.LastUrl);
    }

    [Fact]
    public async Task A_secret_that_is_not_base32_names_the_setting()
    {
        var (flow, _) = Build(new Handler("{}"), secret: "not-base32!");

        var ex = await Assert.ThrowsAsync<DhanSignInException>(() => flow.SignInWithTotpAsync());

        Assert.Equal(DhanSignInFailure.NotSetUp, ex.Failure);
        Assert.Contains("DHAN_TOTP_SECRET", ex.Message);
        Assert.DoesNotContain("!", ex.Message);
    }

    [Fact]
    public async Task A_token_for_another_account_is_not_saved()
    {
        var (flow, sessions) = Build(new Handler("""{"dhanClientId":"9999999999","accessToken":"jwt-other"}"""));

        var ex = await Assert.ThrowsAsync<DhanSignInException>(() => flow.SignInWithTotpAsync());

        Assert.Equal(DhanSignInFailure.Refused, ex.Failure);
        Assert.DoesNotContain("9999999999", ex.Message);
        Assert.Empty(sessions.Saved);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Busy_or_timed_out_is_worth_another_try_not_a_refusal(HttpStatusCode status)
    {
        // A refusal ends the day's automatic tries; a busy minute at Dhan must not.
        var (flow, _) = Build(new Handler("""{"errorMessage":"Too many requests"}""", status));

        var ex = await Assert.ThrowsAsync<DhanSignInException>(() => flow.SignInWithTotpAsync());

        Assert.Equal(DhanSignInFailure.Unreachable, ex.Failure);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))] // how EF reports a transient database failure
    [InlineData(typeof(DbUpdateException))]
    [InlineData(typeof(CryptographicException))]
    public async Task A_token_that_could_not_be_saved_is_worth_another_try_and_names_only_the_type(Type failure)
    {
        var handler = new Handler($$"""{"dhanClientId":"{{ClientId}}","accessToken":"jwt-new"}""");
        var sessions = new Sessions { FailSave = (Exception)Activator.CreateInstance(failure, "Host=db;Password=hunter2")! };
        var (flow, _) = Build(handler, sessions: sessions);

        var ex = await Assert.ThrowsAsync<DhanSignInException>(() => flow.SignInWithTotpAsync());

        Assert.Equal(DhanSignInFailure.Unreachable, ex.Failure);
        Assert.Contains($"could not be saved: {failure.Name}", ex.Message);
        Assert.DoesNotContain("hunter2", ex.Message);
    }

    // ------------------------------------------------------------ when to sign in

    private static readonly DhanAutoSignInSettings Window = new();

    private static DateTime Ist(int day, int hour, int minute, int second = 0) =>
        new DateTime(2026, 9, day, hour, minute, second, DateTimeKind.Utc).AddMinutes(-330);

    private static DateTimeOffset At(DateTime utc) => new(utc, TimeSpan.Zero);

    [Fact]
    public void Signs_in_when_there_is_no_token_on_a_weekday()
    {
        Assert.NotNull(DhanAutoSignInPolicy.ReasonToSignIn(Ist(29, 13, 0), null, Window));
    }

    [Fact]
    public void Never_before_eight_even_with_no_token()
    {
        // A token dead at midnight used to be tried for at 00:00:30, which could
        // spend the day's tries before the morning; nothing streams before 08:45.
        Assert.Null(DhanAutoSignInPolicy.ReasonToSignIn(Ist(29, 0, 0), null, Window));
        Assert.Null(DhanAutoSignInPolicy.ReasonToSignIn(Ist(29, 7, 59), null, Window));
    }

    [Fact]
    public void From_eight_with_no_token_it_signs_in()
    {
        Assert.NotNull(DhanAutoSignInPolicy.ReasonToSignIn(Ist(29, 8, 0), null, Window));
    }

    [Fact]
    public void Never_at_the_weekend()
    {
        // Saturday 26 and Sunday 27 Sep.
        Assert.Null(DhanAutoSignInPolicy.ReasonToSignIn(Ist(26, 8, 10), null, Window));
        Assert.Null(DhanAutoSignInPolicy.ReasonToSignIn(Ist(27, 20, 0), null, Window));
    }

    [Fact]
    public void Not_while_the_last_token_is_live_nor_until_two_minutes_after_it_ends()
    {
        // 30 Sep: 29 Sep's token ended at 08:00:02, the sign-in went at 08:00:11,
        // and Dhan answered "Invalid TOTP".
        var ended = Ist(30, 8, 0, 2);

        Assert.Null(DhanAutoSignInPolicy.ReasonToSignIn(Ist(30, 8, 0, 0), ended, Window));
        Assert.Null(DhanAutoSignInPolicy.ReasonToSignIn(Ist(30, 8, 0, 11), ended, Window));
        Assert.Null(DhanAutoSignInPolicy.ReasonToSignIn(Ist(30, 8, 2, 1), ended, Window));
        Assert.NotNull(DhanAutoSignInPolicy.ReasonToSignIn(Ist(30, 8, 2, 2), ended, Window));
        Assert.Equal(Ist(30, 8, 2, 2), DhanAutoSignInPolicy.EarliestAttemptUtc(Ist(30, 8, 0, 11), ended, Window));
    }

    [Fact]
    public void A_token_that_ended_in_the_night_waits_for_eight()
    {
        // 27 Sep's sign-in at 03:33 ended at 03:33 on Monday 28 Sep.
        var ended = Ist(28, 3, 33);

        Assert.Equal(Ist(28, 8, 0), DhanAutoSignInPolicy.EarliestAttemptUtc(Ist(28, 6, 0), ended, Window));
        Assert.Null(DhanAutoSignInPolicy.ReasonToSignIn(Ist(28, 7, 59), ended, Window));
        Assert.NotNull(DhanAutoSignInPolicy.ReasonToSignIn(Ist(28, 8, 0), ended, Window));
    }

    [Fact]
    public void A_live_token_is_never_replaced_even_one_that_ends_in_the_session()
    {
        // The old rules replaced it between 08:00 and 08:40, or ten minutes before
        // its end: both sign in while a token is live, which is what Dhan refused.
        var endsAtOnePm = Ist(29, 13, 0);

        Assert.Null(DhanAutoSignInPolicy.ReasonToSignIn(Ist(29, 8, 5), endsAtOnePm, Window));
        Assert.Null(DhanAutoSignInPolicy.ReasonToSignIn(Ist(29, 12, 55), endsAtOnePm, Window));
        Assert.Null(DhanAutoSignInPolicy.ReasonToSignIn(Ist(29, 13, 1), endsAtOnePm, Window));
        Assert.NotNull(DhanAutoSignInPolicy.ReasonToSignIn(Ist(29, 13, 2), endsAtOnePm, Window));
    }

    [Fact]
    public void A_token_that_lasts_past_tonight_is_left_alone()
    {
        Assert.Null(DhanAutoSignInPolicy.ReasonToSignIn(Ist(29, 8, 5), Ist(30, 7, 0), Window));
    }

    [Fact]
    public void A_token_that_ends_after_the_window_is_still_replaced_once_it_has()
    {
        // Ended at 08:39: the try is at 08:41, not tomorrow.
        Assert.NotNull(DhanAutoSignInPolicy.ReasonToSignIn(Ist(29, 8, 41), Ist(29, 8, 39), Window));
    }

    [Fact]
    public void The_morning_job_is_told_why_it_must_wait_for_the_last_token()
    {
        string? why = DhanAutoSignInPolicy.WhyNotYet(Ist(30, 8, 0, 11), Ist(30, 8, 0, 2));

        Assert.Contains("ended only at 08:00:02 IST", why);
        Assert.Contains("from 08:02:02 IST", why);
        Assert.Contains("valid until 13:00:00 IST", DhanAutoSignInPolicy.WhyNotYet(Ist(29, 8, 45), Ist(29, 13, 0)));
        Assert.Null(DhanAutoSignInPolicy.WhyNotYet(Ist(30, 8, 2, 2), Ist(30, 8, 0, 2)));
        Assert.Null(DhanAutoSignInPolicy.WhyNotYet(Ist(30, 8, 0), null));
    }

    [Fact]
    public void The_no_token_alert_is_at_ten_past_eight_later_only_for_a_late_start_and_never_after_the_window()
    {
        // Tomorrow's case: the token from the 08:02 Connect ends at 08:02, tries from 08:04.
        Assert.Equal(Ist(30, 8, 10), DhanAutoSignInPolicy.NoTokenAlertUtc(Ist(30, 8, 5), Ist(30, 8, 2), Window));
        Assert.Equal(Ist(30, 8, 10), DhanAutoSignInPolicy.NoTokenAlertUtc(Ist(30, 8, 5), null, Window));
        // A Thursday's drift: ended 08:07:30, tries from 08:09:30, three minutes to finish.
        Assert.Equal(Ist(30, 8, 12, 30), DhanAutoSignInPolicy.NoTokenAlertUtc(Ist(30, 8, 5), Ist(30, 8, 7, 30), Window));
        // Ended at 08:39: still said at 08:40, five minutes before the morning job.
        Assert.Equal(Ist(30, 8, 40), DhanAutoSignInPolicy.NoTokenAlertUtc(Ist(30, 8, 5), Ist(30, 8, 39), Window));
    }

    [Theory]
    [InlineData("Invalid TOTP", DhanRefusal.Code)]
    [InlineData("DH-906 — Invalid TOTP", DhanRefusal.Code)]
    [InlineData("Invalid OTP", DhanRefusal.Code)]
    [InlineData("Invalid pin", DhanRefusal.Pin)]
    [InlineData("DH-905 — Invalid PIN", DhanRefusal.Pin)]
    [InlineData("Invalid PIN or TOTP", DhanRefusal.Pin)] // either could be wrong: the safe side
    [InlineData("Account locked after too many attempts", DhanRefusal.Pin)]
    [InlineData("Invalid credentials", DhanRefusal.Pin)]
    [InlineData("Something went wrong", DhanRefusal.Unrecognised)]
    [InlineData("", DhanRefusal.Unrecognised)]
    public void A_refusal_is_read_as_the_code_the_PIN_or_neither(string reason, DhanRefusal expected)
    {
        Assert.Equal(expected, DhanLoginFlow.ClassifyRefusal(reason));
    }

    [Theory]
    [InlineData(DhanRefusal.Code, 1, true)]
    [InlineData(DhanRefusal.Code, 2, true)]
    [InlineData(DhanRefusal.Code, 3, false)]
    [InlineData(DhanRefusal.Unrecognised, 1, true)]
    [InlineData(DhanRefusal.Unrecognised, 2, false)]
    [InlineData(DhanRefusal.Pin, 1, false)]
    [InlineData(DhanRefusal.Account, 1, false)]
    [InlineData(null, 1, false)]
    public void Only_a_refused_code_earns_fresh_codes(DhanRefusal? refusal, int codesSent, bool again)
    {
        Assert.Equal(again, DhanAutoSignInPolicy.TryAnotherCode(refusal, codesSent));
    }

    [Theory]
    [InlineData(35, 36)]  // the earliest try inside the next minute: woken for it, plus a second
    [InlineData(600, 60)] // further off: the usual minute
    [InlineData(-5, 60)]  // already past
    public void The_worker_wakes_for_the_earliest_try_not_up_to_a_minute_after_it(int secondsAway, int expectedDelay)
    {
        var now = Ist(30, 8, 3, 30);

        var delay = DhanAutoSignInWorker.DelayBeforeNextCheck(now, now.AddSeconds(secondsAway));

        Assert.Equal(TimeSpan.FromSeconds(expectedDelay), delay);
        Assert.Equal(TimeSpan.FromMinutes(1), DhanAutoSignInWorker.DelayBeforeNextCheck(now, null));
    }

    // ------------------------------------------------------------ how often

    [Fact]
    public void A_refusal_stops_the_machine_for_the_day_but_not_the_next()
    {
        var state = new DhanAutoSignInState();
        var now = Ist(29, 8, 5);

        var stop = state.Failed(now, "automatic", "Invalid TOTP", DhanSignInFailure.Refused);

        Assert.Equal(new DhanAutoSignInStop(new DateOnly(2026, 9, 29), "Dhan refused the PIN or the code"), stop);
        Assert.False(state.MayTryAutomatically(now.AddHours(3), out _));
        Assert.True(state.MayTryAutomatically(Ist(30, 8, 0), out _));
    }

    [Fact]
    public void An_unreachable_Dhan_is_tried_again_after_a_pause_three_times_a_day()
    {
        var state = new DhanAutoSignInState();
        var now = Ist(29, 8, 0);

        Assert.Null(state.Failed(now, "automatic", "down", DhanSignInFailure.Unreachable));
        Assert.False(state.MayTryAutomatically(now.AddMinutes(5), out _));
        Assert.True(state.MayTryAutomatically(now.AddMinutes(16), out _));

        Assert.Null(state.Failed(now.AddMinutes(16), "automatic", "down", DhanSignInFailure.Unreachable));
        Assert.NotNull(state.Failed(now.AddMinutes(32), "automatic", "down", DhanSignInFailure.Unreachable));
        Assert.False(state.MayTryAutomatically(now.AddHours(2), out _));
    }

    // ------------------------------------------------------------ the worker

    [Fact]
    public async Task The_worker_does_nothing_without_the_PIN_and_secret()
    {
        var handler = new Handler("{}");
        using var provider = Services(handler, new DhanSettings(), current: null);

        var result = await provider.GetRequiredService<DhanAutoSignInWorker>().CheckOnceAsync(default);

        Assert.Null(result);
        Assert.Equal(string.Empty, handler.LastUrl);
    }

    [Fact]
    public async Task The_worker_signs_in_when_the_day_has_no_token_and_says_so()
    {
        var handler = new Handler($$"""{"dhanClientId":"{{ClientId}}","accessToken":"jwt-new"}""");
        var notifier = new Notifier();
        using var provider = Services(handler, new DhanSettings { Pin = Pin, TotpSecret = Secret }, current: null, notifier);

        var result = await provider.GetRequiredService<DhanAutoSignInWorker>().CheckOnceAsync(default);

        Assert.True(result!.Ok);
        Assert.Contains("generateAccessToken", handler.LastUrl);
        var sent = Assert.Single(notifier.Sent);
        Assert.Equal(NotificationSeverity.Success, sent.Severity);
        Assert.DoesNotContain(Pin, sent.Message);
        Assert.True(provider.GetRequiredService<DhanAutoSignInState>().LastOk);
    }

    [Fact]
    public async Task The_worker_leaves_a_token_that_covers_the_day_alone()
    {
        var handler = new Handler("{}");
        // Signed in at 07:00 IST today: valid until 07:00 tomorrow.
        var current = new BrokerSession { ProviderKey = "dhan", AccessToken = "jwt-live", IsActive = true, UpdatedUtc = Ist(29, 7, 0) };
        using var provider = Services(handler, new DhanSettings { Pin = Pin, TotpSecret = Secret }, current);

        var result = await provider.GetRequiredService<DhanAutoSignInWorker>().CheckOnceAsync(default);

        Assert.Null(result);
        Assert.Equal(string.Empty, handler.LastUrl);
    }

    [Fact]
    public async Task An_unexpected_failure_is_recorded_so_the_pause_and_the_cap_apply()
    {
        // Anything but a DhanSignInException used to escape: nothing recorded,
        // no pause, and the worker asked again a minute later.
        var handler = new Handler("{}");
        var notifier = new Notifier();
        using var provider = Services(handler, AutoOn(), current: null, notifier,
            credentials: new Credentials(ClientId) { Fail = new TimeoutException("Host=db;Password=hunter2") });

        var result = await provider.GetRequiredService<DhanAutoSignInWorker>().CheckOnceAsync(default);

        Assert.False(result!.Ok);
        Assert.Equal(DhanSignInFailure.Unreachable, result.Failure);
        Assert.DoesNotContain("hunter2", result.Message);
        Assert.Equal(NotificationSeverity.Error, Assert.Single(notifier.Sent).Severity);
        Assert.False(provider.GetRequiredService<DhanAutoSignInState>().MayTryAutomatically(TuesdayMorning.UtcDateTime.AddMinutes(5), out _));
    }

    // ------------------------------------------------------------ a stop outlives a restart

    [Fact]
    public async Task A_refusal_is_saved_without_the_PIN_and_holds_across_a_restart()
    {
        var store = new MemoryStore();
        using (var before = Services(new Handler("""{"errorCode":"DH-905","errorMessage":"Invalid pin"}""", HttpStatusCode.BadRequest), AutoOn(), current: null, store: store))
        {
            var refused = await before.GetRequiredService<DhanAutoSignInWorker>().CheckOnceAsync(default);
            Assert.Equal(DhanSignInFailure.Refused, refused!.Failure);
        }

        string saved = store.Values[SystemSettingKeys.DhanAutoSignInStopped];
        Assert.StartsWith("2026-09-29: ", saved);
        Assert.DoesNotContain(Pin, saved);

        // The 08:45 restart: a new process, a new in-memory state, the same database.
        var handler = new Handler($$"""{"dhanClientId":"{{ClientId}}","accessToken":"jwt-new"}""");
        using var after = Services(handler, AutoOn(), current: null, store: store);
        var result = await after.GetRequiredService<DhanAutoSignInWorker>().CheckOnceAsync(default);

        Assert.False(result!.Tried);
        Assert.Equal(0, handler.Calls);
        Assert.True(after.GetRequiredService<DhanAutoSignInState>().StoppedFor(TuesdayMorning.UtcDateTime));
    }

    [Fact]
    public async Task A_stop_saved_on_an_earlier_day_is_ignored()
    {
        var store = new MemoryStore { Values = { [SystemSettingKeys.DhanAutoSignInStopped] = "2026-09-28: Dhan refused the PIN or the code" } };
        var handler = new Handler($$"""{"dhanClientId":"{{ClientId}}","accessToken":"jwt-new"}""");
        using var provider = Services(handler, AutoOn(), current: null, store: store);

        var result = await provider.GetRequiredService<DhanAutoSignInWorker>().CheckOnceAsync(default);

        Assert.True(result!.Ok);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_stop_that_cannot_be_read_holds_the_machine_back_but_not_a_person()
    {
        // Skipping costs a morning of pressing Connect; trying could lock the account.
        var store = new MemoryStore { Broken = true };
        var handler = new Handler($$"""{"dhanClientId":"{{ClientId}}","accessToken":"jwt-new"}""");
        using var provider = Services(handler, AutoOn(), current: null, store: store);

        var automatic = await provider.GetRequiredService<DhanAutoSignInWorker>().CheckOnceAsync(default);
        Assert.False(automatic!.Tried);
        Assert.Equal(0, handler.Calls);

        using var scope = provider.CreateScope();
        var console = await scope.ServiceProvider.GetRequiredService<DhanAutoSignInService>().SignInAsync("console", "test", default);
        Assert.True(console.Ok);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Sign_in_now_after_a_refusal_clears_the_saved_stop()
    {
        var store = new MemoryStore { Values = { [SystemSettingKeys.DhanAutoSignInStopped] = "2026-09-29: Dhan refused the PIN or the code" } };
        var handler = new Handler($$"""{"dhanClientId":"{{ClientId}}","accessToken":"jwt-new"}""");
        using var provider = Services(handler, AutoOn(), current: null, store: store);

        using var scope = provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<DhanAutoSignInService>().SignInAsync("console", "the PIN was corrected", default);

        Assert.True(result.Ok);
        Assert.False(store.Values.ContainsKey(SystemSettingKeys.DhanAutoSignInStopped));
    }

    [Theory]
    [InlineData("2026-09-29: Dhan refused the PIN or the code", true)]
    [InlineData("2026-09-28: Dhan refused the PIN or the code", false)]
    public async Task The_stop_is_taken_back_at_startup_for_its_own_day_only(string stored, bool stopped)
    {
        var store = new MemoryStore { Values = { [SystemSettingKeys.DhanAutoSignInStopped] = stored } };
        using var provider = Services(new Handler("{}"), AutoOn(), current: null, store: store);

        await provider.GetRequiredService<DhanAutoSignInWorker>().RestoreAsync(default);

        var state = provider.GetRequiredService<DhanAutoSignInState>();
        Assert.Equal(stopped, state.StoppedFor(TuesdayMorning.UtcDateTime));
        if (stopped) Assert.Contains("Dhan refused the PIN or the code", state.LastMessage);
    }

    // ------------------------------------------------------------ one code, one sign-in

    [Theory]
    [InlineData(10, null, 0)]      // early in the step, nothing sent before
    [InlineData(27, null, 4)]      // about to roll over: one second into the next step
    [InlineData(10, 0L, 21)]       // this step's code already went: the next step
    [InlineData(27, 0L, 4)]        // both at once: still the next step
    [InlineData(10, -1L, 0)]       // the last code was from the step before
    public void A_code_is_read_early_in_a_step_the_last_code_did_not_come_from(int intoStep, long? lastRelative, int expectedWait)
    {
        var stepStart = DateTimeOffset.FromUnixTimeSeconds(DhanLoginFlow.CodeStep(TuesdayMorning) * 30);
        long? last = lastRelative is { } r ? DhanLoginFlow.CodeStep(stepStart) + r : null;

        var wait = DhanLoginFlow.WaitBeforeCode(stepStart.AddSeconds(intoStep), last);

        Assert.Equal(TimeSpan.FromSeconds(expectedWait), wait);
    }

    [Fact]
    public async Task Two_sign_ins_in_one_step_send_two_different_codes()
    {
        var clock = new JumpingClock(TuesdayMorning);
        var handler = new Handler($$"""{"dhanClientId":"{{ClientId}}","accessToken":"jwt-new"}""");
        using var provider = Services(handler, AutoOn(), current: null, clock: clock);

        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DhanAutoSignInService>();
        await service.SignInAsync("console", "first", default);
        await service.SignInAsync("console", "second", default);

        Assert.Equal(2, handler.Urls.Count);
        Assert.EndsWith($"totp={Totp.Generate(Secret, TuesdayMorning)}", handler.Urls[0]);
        Assert.EndsWith($"totp={Totp.Generate(Secret, TuesdayMorning.AddSeconds(30))}", handler.Urls[1]);
        Assert.Equal(TimeSpan.FromSeconds(21), clock.Waited);
    }

    [Fact]
    public async Task A_machine_that_decided_before_a_sign_in_gets_that_one_not_a_new_code()
    {
        // The worker read "no token" before the morning job's sign-in finished;
        // it must not send a second code on the strength of that stale look.
        var handler = new Handler($$"""{"dhanClientId":"{{ClientId}}","accessToken":"jwt-new"}""");
        using var provider = Services(handler, AutoOn(), current: null);
        using (var scope = provider.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<DhanAutoSignInService>().SignInAsync("morning job", "test", default)).Ok);

        var result = await provider.GetRequiredService<DhanAutoSignInWorker>().CheckOnceAsync(default);

        Assert.True(result!.Ok);
        Assert.False(result.Tried);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)] // refused: stopped for the day
    [InlineData(HttpStatusCode.BadGateway)] // not reached: the 15-minute pause, which is not saved
    public async Task A_machine_that_waited_while_a_sign_in_failed_does_not_try_again(HttpStatusCode status)
    {
        var handler = new Handler("""{"errorMessage":"Invalid pin"}""", status);
        using var provider = Services(handler, AutoOn(), current: null);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<DhanAutoSignInService>();

        await service.SignInAsync("automatic", "test", default);
        var second = await service.SignInAsync("morning job", "decided before the refusal", default);

        Assert.False(second.Tried);
        Assert.Equal(1, handler.Calls);
    }

    // ------------------------------------------------------------ a refused code, and 30 Sep

    private const string InvalidTotp = """{"status":"failure","remarks":"Invalid TOTP"}""";
    private static readonly string Issued = $$"""{"dhanClientId":"{{ClientId}}","accessToken":"jwt-new"}""";

    /// <summary>29 Sep's token, taken at 08:00:02 IST, which ended at 08:00:02 on 30 Sep.</summary>
    private static BrokerSession TokenTakenAt(DateTime utc) =>
        new() { ProviderKey = "dhan", AccessToken = "jwt-earlier", IsActive = true, UpdatedUtc = utc };

    [Fact]
    public async Task The_30_Sep_morning_waits_for_the_last_token_then_a_refused_code_is_retried_with_a_new_one()
    {
        var clock = new JumpingClock(At(Ist(30, 8, 0, 11)));
        var handler = new Handler(InvalidTotp).Then(Issued);
        var store = new MemoryStore();
        var notifier = new Notifier();
        using var provider = Services(handler, AutoOn(), TokenTakenAt(Ist(29, 8, 0, 2)), notifier, store, clock);
        var worker = provider.GetRequiredService<DhanAutoSignInWorker>();

        // 08:00:11, when Dhan refused on 30 Sep, and one second before 08:02:02: Dhan is not asked.
        Assert.Null(await worker.CheckOnceAsync(default));
        clock.Set(Ist(30, 8, 2, 1));
        Assert.Null(await worker.CheckOnceAsync(default));
        Assert.Equal(0, handler.Calls);

        clock.Set(Ist(30, 8, 2, 2));
        var result = await worker.CheckOnceAsync(default);

        Assert.True(result!.Ok);
        Assert.Equal(2, handler.Calls);
        // 08:02:02 is two seconds into its step; the retry waits for one second into the next.
        Assert.Equal(Totp.Generate(Secret, At(Ist(30, 8, 2, 2))), handler.Codes[0]);
        Assert.Equal(Totp.Generate(Secret, At(Ist(30, 8, 2, 31))), handler.Codes[1]);
        Assert.NotEqual(handler.Codes[0], handler.Codes[1]);
        Assert.False(store.Values.ContainsKey(SystemSettingKeys.DhanAutoSignInStopped));
        Assert.False(provider.GetRequiredService<DhanAutoSignInState>().StoppedFor(Ist(30, 8, 3)));
        var sent = Assert.Single(notifier.Sent);
        Assert.Equal(NotificationSeverity.Success, sent.Severity);
        Assert.Contains("Invalid TOTP", sent.Message);
    }

    [Fact]
    public async Task Three_refused_codes_stop_the_day_each_code_from_a_new_step_within_two_minutes()
    {
        var clock = new JumpingClock(TuesdayMorning); // 08:05:10, ten seconds into a step
        var handler = new Handler(InvalidTotp).Then(InvalidTotp).Then(InvalidTotp).Then(Issued);
        var store = new MemoryStore();
        using var provider = Services(handler, AutoOn(), current: null, store: store, clock: clock);

        var result = await provider.GetRequiredService<DhanAutoSignInWorker>().CheckOnceAsync(default);

        Assert.False(result!.Ok);
        Assert.Equal(DhanSignInFailure.Refused, result.Failure);
        Assert.Equal(
            new[] { TuesdayMorning, TuesdayMorning.AddSeconds(21), TuesdayMorning.AddSeconds(51) }.Select(t => Totp.Generate(Secret, t)),
            handler.Codes);
        Assert.Equal(3, handler.Codes.Distinct().Count());
        Assert.True(clock.Waited < TimeSpan.FromMinutes(2));
        Assert.Equal("2026-09-29: Dhan refused 3 TOTP codes in a row", store.Values[SystemSettingKeys.DhanAutoSignInStopped]);

        // The machine has stopped for the day ...
        Assert.Null(await provider.GetRequiredService<DhanAutoSignInWorker>().CheckOnceAsync(default));
        Assert.Equal(3, handler.Calls);

        // ... a person is not held back, and still gets a code never sent before.
        using var scope = provider.CreateScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<DhanAutoSignInService>().SignInAsync("console", "test", default)).Ok);
        Assert.Equal(4, handler.Codes.Distinct().Count());
    }

    [Fact]
    public async Task A_refused_PIN_stops_the_day_at_once_with_no_second_code()
    {
        var handler = new Handler("""{"errorCode":"DH-905","errorMessage":"Invalid PIN"}""", HttpStatusCode.BadRequest).Then(Issued);
        var store = new MemoryStore();
        using var provider = Services(handler, AutoOn(), current: null, store: store, clock: new JumpingClock(TuesdayMorning));

        var result = await provider.GetRequiredService<DhanAutoSignInWorker>().CheckOnceAsync(default);

        Assert.False(result!.Ok);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("2026-09-29: Dhan refused the PIN", store.Values[SystemSettingKeys.DhanAutoSignInStopped]);
    }

    [Fact]
    public async Task An_unrecognised_refusal_gets_one_more_code_then_stops()
    {
        var handler = new Handler("""{"remarks":"Something went wrong"}""").Then("""{"remarks":"Something went wrong"}""").Then(Issued);
        var store = new MemoryStore();
        using var provider = Services(handler, AutoOn(), current: null, store: store, clock: new JumpingClock(TuesdayMorning));

        var result = await provider.GetRequiredService<DhanAutoSignInWorker>().CheckOnceAsync(default);

        Assert.False(result!.Ok);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, handler.Codes.Distinct().Count());
        Assert.True(store.Values.ContainsKey(SystemSettingKeys.DhanAutoSignInStopped));
    }

    [Fact]
    public async Task The_28_Sep_console_press_is_not_held_back_and_now_retries_a_refused_code()
    {
        // Monday 28 Sep, about 07:55: a person pressed Sign in now before the
        // window, with the last token still live. A person is never held back;
        // the only change is that "Invalid TOTP" gets a fresh code.
        var clock = new JumpingClock(At(Ist(28, 7, 55, 5)));
        var handler = new Handler(InvalidTotp).Then(Issued);
        var store = new MemoryStore();
        using var provider = Services(handler, AutoOn(), TokenTakenAt(Ist(27, 12, 0)), store: store, clock: clock);

        var result = await SignInNow(provider, "console");

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal(2, handler.Calls);
        Assert.False(store.Values.ContainsKey(SystemSettingKeys.DhanAutoSignInStopped));
    }

    [Fact]
    public async Task The_28_Sep_console_press_that_Dhan_keeps_refusing_still_stops_the_day()
    {
        var handler = new Handler(InvalidTotp);
        var store = new MemoryStore();
        using var provider = Services(handler, AutoOn(), current: null, store: store, clock: new JumpingClock(At(Ist(28, 7, 55, 5))));

        var result = await SignInNow(provider, "console");

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Equal(3, handler.Calls);
        Assert.StartsWith("2026-09-28: ", store.Values[SystemSettingKeys.DhanAutoSignInStopped]);
    }

    [Fact]
    public async Task The_morning_job_is_held_back_while_the_last_token_is_live()
    {
        // A Connect pressed at 13:00 yesterday: live until 13:00 today. The
        // 08:45 job asks to replace it; Dhan is not asked, the job asks for Connect.
        var handler = new Handler(Issued);
        using var provider = Services(handler, AutoOn(), TokenTakenAt(Ist(28, 13, 0)));

        var morning = await SignInNow(provider, "morning job");
        Assert.Equal(StatusCodes.Status409Conflict, morning.StatusCode);
        Assert.Equal(0, handler.Calls);

        Assert.Equal(StatusCodes.Status200OK, (await SignInNow(provider, "console")).StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task No_token_by_ten_past_eight_is_said_once()
    {
        // The day stopped at 08:05 on a refused PIN; that failure was said then.
        var clock = new JumpingClock(TuesdayMorning);
        var notifier = new Notifier();
        var handler = new Handler("""{"errorMessage":"Invalid PIN"}""", HttpStatusCode.BadRequest);
        using var provider = Services(handler, AutoOn(), current: null, notifier, clock: clock);
        var worker = provider.GetRequiredService<DhanAutoSignInWorker>();

        await worker.CheckOnceAsync(default);
        clock.Set(Ist(29, 8, 9, 59));
        await worker.CheckOnceAsync(default);
        Assert.Single(notifier.Sent);

        clock.Set(Ist(29, 8, 10));
        await worker.CheckOnceAsync(default);
        clock.Set(Ist(29, 8, 11));
        await worker.CheckOnceAsync(default);

        Assert.Equal(2, notifier.Sent.Count);
        var alert = notifier.Sent[1];
        Assert.Equal(NotificationSeverity.Error, alert.Severity);
        Assert.Contains("press Connect", alert.Title);
        Assert.Contains("before 08:45", alert.Message);
        Assert.DoesNotContain(Pin, alert.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task No_alert_with_a_live_token_or_at_the_weekend()
    {
        var notifier = new Notifier();
        using (var live = Services(new Handler(Issued), AutoOn(), TokenTakenAt(Ist(29, 8, 3)), notifier, clock: new JumpingClock(At(Ist(29, 8, 15)))))
            await live.GetRequiredService<DhanAutoSignInWorker>().CheckOnceAsync(default);

        using (var saturday = Services(new Handler(Issued), AutoOn(), current: null, notifier, clock: new JumpingClock(At(Ist(26, 8, 15)))))
            await saturday.GetRequiredService<DhanAutoSignInWorker>().CheckOnceAsync(default);

        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task Each_code_is_logged_with_its_moment_the_last_token_and_Dhans_reason_but_never_the_secrets()
    {
        var log = new ListLogger<DhanAutoSignInService>();
        var handler = new Handler(InvalidTotp).Then(Issued);
        using var provider = Services(handler, AutoOn(), TokenTakenAt(Ist(29, 8, 0, 2)),
            clock: new JumpingClock(At(Ist(30, 8, 2, 2))), serviceLog: log);

        await provider.GetRequiredService<DhanAutoSignInWorker>().CheckOnceAsync(default);

        Assert.Equal(2, log.Lines.Count);
        Assert.Contains("read at 08:02:02.0 IST, 2.0 s into its TOTP step", log.Lines[0]);
        Assert.Contains("the last token had ended 2 min 0 s earlier (at 08:00:02 IST)", log.Lines[0]);
        Assert.Contains("Invalid TOTP", log.Lines[0]);
        Assert.Contains("read at 08:02:31.0 IST, 1.0 s into its TOTP step", log.Lines[1]);
        foreach (string line in log.Lines)
        {
            Assert.DoesNotContain(Pin, line);
            Assert.DoesNotContain(Secret, line);
            foreach (string code in handler.Codes) Assert.DoesNotContain(code, line);
        }
    }

    // ------------------------------------------------------------ the morning job's call

    [Fact]
    public async Task The_morning_job_is_refused_when_the_automatic_sign_in_is_switched_off()
    {
        var handler = new Handler($$"""{"dhanClientId":"{{ClientId}}","accessToken":"jwt-new"}""");
        var settings = AutoOn();
        settings.AutoSignIn.Enabled = false;
        using var provider = Services(handler, settings, current: null);

        var morning = await SignInNow(provider, "morning job");
        Assert.Equal(StatusCodes.Status409Conflict, morning.StatusCode);
        Assert.Equal(0, handler.Calls);

        // A person can still sign in with the switch off.
        var console = await SignInNow(provider, "console");
        Assert.Equal(StatusCodes.Status200OK, console.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task The_morning_job_is_held_back_by_a_stop_saved_before_the_restart()
    {
        var store = new MemoryStore { Values = { [SystemSettingKeys.DhanAutoSignInStopped] = "2026-09-29: Dhan refused the PIN or the code" } };
        var handler = new Handler($$"""{"dhanClientId":"{{ClientId}}","accessToken":"jwt-new"}""");
        using var provider = Services(handler, AutoOn(), current: null, store: store);

        var result = await SignInNow(provider, "morning job");

        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_morning_job_that_hangs_up_does_not_cancel_the_sign_in()
    {
        // curl --max-time 30 gives up; the token Dhan issued must still be saved.
        var handler = new Handler($$"""{"dhanClientId":"{{ClientId}}","accessToken":"jwt-new"}""");
        var sessions = new Sessions();
        using var provider = Services(handler, AutoOn(), current: null, sessions: sessions);

        var result = await SignInNow(provider, "morning job", requestAborted: new CancellationToken(canceled: true));

        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal("jwt-new", Assert.Single(sessions.Saved).AccessToken);
    }

    // ------------------------------------------------------------ a pasted token

    [Fact]
    public async Task An_expired_pasted_token_is_not_served_and_the_reason_says_when_it_died()
    {
        var api = ApiWithConfiguredToken(Jwt(DateTimeOffset.UtcNow.AddDays(-12)));

        var ex = await Assert.ThrowsAsync<DhanApiException>(() => api.ResolveTokenAsync());

        Assert.Contains("DHAN_ACCESS_TOKEN", ex.Message);
        Assert.Contains("expired", ex.Message);
    }

    [Fact]
    public async Task A_live_pasted_token_is_served_with_its_end()
    {
        var ends = DateTimeOffset.UtcNow.AddHours(5);
        var api = ApiWithConfiguredToken(Jwt(ends));

        var (_, token) = await api.ResolveTokenAsync();

        Assert.Equal("configuration", token.Source);
        Assert.Equal(ends.ToUnixTimeSeconds(), new DateTimeOffset(token.ExpiresUtc!.Value).ToUnixTimeSeconds());
    }

    // ---------------------------------------------------------------- fixtures

    private static (DhanLoginFlow Flow, Sessions Sessions) Build(Handler handler, string pin = Pin, string secret = Secret, Sessions? sessions = null)
    {
        sessions ??= new Sessions();
        var flow = new DhanLoginFlow(
            new SettingsMonitor(new DhanSettings { Pin = pin, TotpSecret = secret }),
            new Credentials(ClientId),
            sessions,
            new Factory(handler),
            NullLogger<DhanLoginFlow>.Instance,
            new FixedTime(TuesdayMorning));
        return (flow, sessions);
    }

    private static DhanSettings AutoOn() => new() { Pin = Pin, TotpSecret = Secret };

    private static ServiceProvider Services(
        Handler handler,
        DhanSettings settings,
        BrokerSession? current,
        Notifier? notifier = null,
        MemoryStore? store = null,
        TimeProvider? clock = null,
        Credentials? credentials = null,
        Sessions? sessions = null,
        ILogger<DhanAutoSignInService>? serviceLog = null)
    {
        sessions ??= new Sessions();
        sessions.Current = current;
        var services = new ServiceCollection();
        services.AddSingleton<IOptionsMonitor<DhanSettings>>(new SettingsMonitor(settings));
        services.AddSingleton<IBrokerCredentialsProvider>(credentials ?? new Credentials(ClientId));
        services.AddSingleton<IBrokerSessionStore>(sessions);
        services.AddSingleton<IHttpClientFactory>(new Factory(handler));
        services.AddSingleton<ISystemNotifier>(notifier ?? new Notifier());
        services.AddSingleton<IProcessSettingsStore>(store ?? new MemoryStore());
        services.AddSingleton<TimeProvider>(clock ?? new FixedTime(TuesdayMorning));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<DhanAutoSignInState>();
        services.AddScoped(sp => new DhanLoginFlow(
            sp.GetRequiredService<IOptionsMonitor<DhanSettings>>(), sp.GetRequiredService<IBrokerCredentialsProvider>(),
            sp.GetRequiredService<IBrokerSessionStore>(), sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<DhanLoginFlow>.Instance, sp.GetRequiredService<TimeProvider>()));
        services.AddScoped(sp => new DhanAutoSignInService(
            sp.GetRequiredService<DhanLoginFlow>(), sp.GetRequiredService<DhanAutoSignInState>(),
            sp.GetRequiredService<IProcessSettingsStore>(), sp.GetRequiredService<IBrokerSessionStore>(),
            sp.GetRequiredService<ISystemNotifier>(),
            serviceLog ?? NullLogger<DhanAutoSignInService>.Instance, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new DhanAutoSignInWorker(
            sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IOptionsMonitor<DhanSettings>>(),
            sp.GetRequiredService<DhanAutoSignInState>(), NullLogger<DhanAutoSignInWorker>.Instance, sp.GetRequiredService<TimeProvider>()));
        return services.BuildServiceProvider();
    }

    /// <summary>POST /api/Dhan/auto-sign-in as the endpoint runs it; only the auto sign-in action is exercised.</summary>
    private static async Task<IStatusCodeActionResult> SignInNow(ServiceProvider provider, string trigger, CancellationToken requestAborted = default)
    {
        var controller = new DhanController(null!, null!, null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestAborted = requestAborted } },
        };
        using var scope = provider.CreateScope();
        var result = await controller.AutoSignInNow(
            scope.ServiceProvider.GetRequiredService<DhanAutoSignInService>(),
            provider.GetRequiredService<IOptionsMonitor<DhanSettings>>(),
            trigger);
        return Assert.IsAssignableFrom<IStatusCodeActionResult>(result);
    }

    private static DhanApiClient ApiWithConfiguredToken(string token) => new(
        Options.Create(new DhanSettings { AccessToken = token }),
        new Credentials(ClientId),
        new Sessions(),
        new Factory(new Handler("{}")),
        new DhanRateGate(),
        NullLogger<DhanApiClient>.Instance);

    private static string Jwt(DateTimeOffset expires)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part("""{"alg":"HS512","typ":"JWT"}""")}.{Part($$"""{"iss":"dhan","dhanClientId":"{{ClientId}}","exp":{{expires.ToUnixTimeSeconds()}}}""")}.c2lnbmF0dXJl";
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// A clock whose timers fire at once and move it on by their delay, so a
    /// wait for the next TOTP step takes no real time and can be measured.
    /// </summary>
    private sealed class JumpingClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public TimeSpan Waited { get; private set; }

        public override DateTimeOffset GetUtcNow() => _now;

        /// <summary>The next minute the worker looks, say.</summary>
        public void Set(DateTime utc) => _now = new DateTimeOffset(utc, TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                _now += dueTime;
                Waited += dueTime;
                callback(state);
            }
            return new NoTimer();
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class SettingsMonitor(DhanSettings value) : IOptionsMonitor<DhanSettings>
    {
        public DhanSettings CurrentValue => value;
        public DhanSettings Get(string? name) => value;
        public IDisposable? OnChange(Action<DhanSettings, string?> listener) => null;
    }

    /// <summary>Dhan's side: answers in the order given, the last one again once they run out.</summary>
    private sealed class Handler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        private readonly List<(string Body, HttpStatusCode Status)> _answers = [(body, status)];

        public List<string> Urls { get; } = new();
        public int Calls => Urls.Count;
        public string LastUrl => Urls.Count == 0 ? string.Empty : Urls[^1];
        public string LastMethod { get; private set; } = string.Empty;

        /// <summary>The TOTP code each request carried, in order.</summary>
        public List<string> Codes => Urls.Select(u => u[(u.LastIndexOf("totp=", StringComparison.Ordinal) + 5)..]).ToList();

        public Handler Then(string nextBody, HttpStatusCode nextStatus = HttpStatusCode.OK)
        {
            _answers.Add((nextBody, nextStatus));
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (answer, code) = _answers[Math.Min(Urls.Count, _answers.Count - 1)];
            Urls.Add(request.RequestUri!.OriginalString);
            LastMethod = request.Method.Method;
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(answer, Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>Every formatted line, so a test can read what was logged.</summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Credentials(string clientId) : IBrokerCredentialsProvider
    {
        /// <summary>Thrown by every read, like a credentials table that cannot be reached.</summary>
        public Exception? Fail { get; init; }

        public Task<BrokerCredentials> GetAsync(string providerKey, long? brokerAccountId = null, CancellationToken cancellationToken = default)
            => Fail is not null
                ? Task.FromException<BrokerCredentials>(Fail)
                : Task.FromResult(new BrokerCredentials(clientId, "secret-456", string.Empty, null, "test", null, null));

        public Task SaveAsync(string providerKey, string clientId, string secretKey, string redirectUri, string updatedBy,
            string? tradingPin = null, long? brokerAccountId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class Sessions : IBrokerSessionStore
    {
        public List<BrokerSession> Saved { get; } = new();
        public BrokerSession? Current { get; set; }

        /// <summary>Thrown by every save, like a database or key ring that fails after Dhan issued the token.</summary>
        public Exception? FailSave { get; init; }

        public Task<BrokerSession?> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult<BrokerSession?>(null);
        public Task<BrokerSession?> GetForAccountAsync(long brokerAccountId, CancellationToken cancellationToken = default) => Task.FromResult<BrokerSession?>(null);
        public Task ClearAccountAsync(long brokerAccountId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BrokerSession?> GetForProviderAsync(string providerKey, CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public Task ClearAsync(string? providerKey = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(BrokerSession session, CancellationToken cancellationToken = default)
        {
            if (FailSave is not null) return Task.FromException(FailSave);
            Saved.Add(session);
            return Task.CompletedTask;
        }
    }

    /// <summary>system_settings in memory; <see cref="Broken"/> fails every call, like a database that is down.</summary>
    private sealed class MemoryStore : IProcessSettingsStore
    {
        public Dictionary<string, string> Values { get; init; } = new();
        public bool Broken { get; init; }

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Broken ? Task.FromException<string?>(Down()) : Task.FromResult(Values.GetValueOrDefault(key));

        public Task SetAsync(string key, string value, string? updatedBy = null, CancellationToken cancellationToken = default)
        {
            if (Broken) return Task.FromException(Down());
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) =>
            Broken ? Task.FromException<bool>(Down()) : Task.FromResult(Values.Remove(key));

        public Task<int?> GetPidAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPidAsync(string key, int processId, string? updatedBy = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteIfPidAsync(string key, int processId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private static InvalidOperationException Down() => new("the database is down");
    }

    private sealed class Notifier : ISystemNotifier
    {
        public List<(NotificationSeverity Severity, string Title, string Message)> Sent { get; } = new();

        public Task NotifyAsync(NotificationCategory category, NotificationSeverity severity, string title, string message,
            string? underlying = null, string? symbol = null, long? simulationRunId = null, CancellationToken cancellationToken = default)
        {
            Sent.Add((severity, title, message));
            return Task.CompletedTask;
        }
    }
}
