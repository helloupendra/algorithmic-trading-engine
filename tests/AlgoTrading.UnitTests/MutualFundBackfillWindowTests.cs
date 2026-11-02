using System.Net;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.MutualFunds;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The NAV history backfill stays out of the desk's way. The 2 Nov 2026 review found it
/// could run into the NSE session: a failed request was retried three times, each up to
/// 20 minutes, without asking again; and it ignored the 08:45 start-up and the MCX evening.
/// </summary>
public class MutualFundBackfillWindowTests
{
    private static readonly Func<DateOnly, bool> Weekdays = d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    /// <summary>MCX open 09:00–23:55 IST on weekdays, as in the US winter.</summary>
    private static bool McxOpen(DateTime utc)
    {
        var ist = IstTime.ToIst(utc);
        return Weekdays(DateOnly.FromDateTime(ist)) && ist.TimeOfDay >= new TimeSpan(9, 0, 0) && ist.TimeOfDay < new TimeSpan(23, 55, 0);
    }

    private static DateTime Ist(int day, int hour, int minute = 0) =>
        new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Utc).AddMinutes(-330);

    private static bool MustWait(DateTime utc) => MutualFundSchedule.BackfillMustWait(utc, Weekdays, McxOpen);

    // ------------------------------------------------------------ when

    [Theory]
    [InlineData(3, 1, 0, false)]    // Tuesday night, MCX closed
    [InlineData(3, 8, 39, false)]
    [InlineData(3, 8, 40, true)]    // ahead of the 08:45 morning job
    [InlineData(3, 8, 59, true)]
    [InlineData(3, 12, 0, true)]    // NSE
    [InlineData(3, 16, 0, true)]    // NSE is done, MCX is not
    [InlineData(3, 23, 54, true)]
    [InlineData(3, 23, 55, false)]  // the MCX close
    [InlineData(8, 11, 0, false)]   // a Sunday
    public void When_the_backfill_must_wait(int day, int hour, int minute, bool wait)
    {
        Assert.Equal(wait, MustWait(Ist(day, hour, minute)));
    }

    [Fact]
    public void An_nse_holiday_with_mcx_shut_in_the_morning_leaves_the_morning_free()
    {
        Assert.False(MutualFundSchedule.BackfillMustWait(Ist(3, 10), _ => false, _ => false));
    }

    [Fact]
    public void Mcx_in_session_comes_from_the_calendar()
    {
        var sessions = new MarketSessionService(new MarketIntelligenceTestKit.Calendar());

        Assert.True(MutualFundSchedule.BackfillMustWait(Ist(3, 22), sessions));   // Tuesday 22:00: crude is live
        Assert.False(MutualFundSchedule.BackfillMustWait(Ist(4, 1), sessions));   // 01:00: it has closed
        Assert.False(MutualFundSchedule.BackfillMustWait(Ist(8, 22), sessions));  // Sunday
    }

    [Fact]
    public void A_request_is_not_started_just_before_the_edge()
    {
        var sessions = new MarketSessionService(new MarketIntelligenceTestKit.Calendar());

        Assert.True(MutualFundSchedule.BackfillMayStart(Ist(3, 8, 30), sessions));
        Assert.False(MutualFundSchedule.BackfillMayStart(Ist(3, 8, 36), sessions));
        Assert.False(MutualFundSchedule.BackfillMayStart(Ist(3, 9), sessions));
    }

    [Theory]
    [InlineData(3, 1, 0, 20)]       // deep in the night: only the request's own deadline
    [InlineData(3, 8, 25, 14)]      // 08:39: the last whole minute before 08:40
    [InlineData(3, 8, 38, 1)]
    [InlineData(3, 8, 40, 0)]
    public void The_runway_never_reaches_the_quiet(int day, int hour, int minute, int minutes)
    {
        var now = Ist(day, hour, minute);
        var runway = MutualFundSchedule.BackfillRunway(now, MutualFundHistory.RequestDeadline, MustWait);

        Assert.Equal(TimeSpan.FromMinutes(minutes), runway);
        Assert.False(runway > TimeSpan.Zero && MustWait(now + runway));
    }

    [Fact]
    public void The_runway_never_passes_the_edge_when_the_clock_is_between_minutes()
    {
        var now = Ist(3, 8, 22).AddSeconds(30);
        var runway = MutualFundSchedule.BackfillRunway(now, MutualFundHistory.RequestDeadline, MustWait);

        Assert.True(now + runway < Ist(3, 8, 40));
    }

    // ------------------------------------------------------------ retries

    [Fact]
    public async Task A_failed_request_is_retried_while_the_backfill_may_run()
    {
        var rig = await Rig.CreateAsync();

        var state = await rig.History.RunAsync(() => true, default);

        Assert.Equal(4, rig.Attempts);
        Assert.Equal(3, rig.Sleeps.Count);
        Assert.Equal("failed", state.State);
        Assert.NotNull(state.LastError);
    }

    [Fact]
    public async Task A_quiet_window_that_began_during_the_request_stops_the_retries_and_is_no_failure()
    {
        var rig = await Rig.CreateAsync();
        int asked = 0;

        // The review's probe turned round: it made 4 attempts and 3 sleeps, and failed.
        var state = await rig.History.RunAsync(() => asked++ < 2, default);

        Assert.Equal(1, rig.Attempts);
        Assert.Empty(rig.Sleeps);
        Assert.Equal("waiting", state.State);
        Assert.True(state.Requested);
        Assert.Null(state.LastError);
        Assert.True(MutualFundHistory.ReadyToRun(state, DateTime.UtcNow), "no failure pause: it carries on once it may");
    }

    [Fact]
    public async Task A_quiet_window_that_began_during_the_wait_stops_before_the_next_try()
    {
        var rig = await Rig.CreateAsync();
        bool quiet = false;
        rig.History.Sleep = (_, _) =>
        {
            quiet = true;
            return Task.CompletedTask;
        };

        var state = await rig.History.RunAsync(() => !quiet, default);

        Assert.Equal(1, rig.Attempts);
        Assert.Equal("waiting", state.State);
    }

    [Fact]
    public async Task A_pause_during_the_retries_is_a_pause()
    {
        var rig = await Rig.CreateAsync();
        rig.History.Sleep = (_, _) =>
        {
            rig.Status.StopBackfill = true;
            return Task.CompletedTask;
        };

        var state = await rig.History.RunAsync(() => !rig.Status.StopBackfill, default);

        Assert.Equal(1, rig.Attempts);
        Assert.Equal("paused", state.State);
        Assert.False(state.Requested);
    }

    // ------------------------------------------------------------ the bound

    [Fact]
    public async Task A_request_that_stalls_is_cut_at_the_runway_not_at_its_twenty_minutes()
    {
        var hanging = new Hanging();
        var rig = await Rig.CreateAsync(hanging);

        var run = rig.History.RunAsync(() => !hanging.WasCut, default, () => TimeSpan.FromMilliseconds(50));
        var state = await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(hanging.WasCut);
        Assert.Equal(1, hanging.Requests);
        Assert.Equal("waiting", state.State);
        Assert.Null(state.LastError);
    }

    [Fact]
    public async Task No_runway_left_means_no_request_goes_to_amfi()
    {
        var hanging = new Hanging();
        var rig = await Rig.CreateAsync(hanging);
        int asked = 0;

        var state = await rig.History.RunAsync(() => asked++ < 2, default, () => TimeSpan.Zero);

        Assert.Equal(0, hanging.Requests);
        Assert.Equal("waiting", state.State);
    }

    // ------------------------------------------------------------------

    private sealed class Rig
    {
        public required MutualFundHistory History { get; init; }
        public required MutualFundStatus Status { get; init; }
        public required Failing? Http { get; init; }
        public List<TimeSpan> Sleeps { get; } = new();
        public int Attempts => Http?.Attempts ?? 0;

        public static async Task<Rig> CreateAsync(HttpMessageHandler? handler = null)
        {
            var settings = new MemorySettings();
            await MutualFundSettings.SaveBackfillAsync(settings, new MutualFundBackfillState { Requested = true }, default);

            var failing = handler is null ? new Failing() : null;
            var status = new MutualFundStatus();
            var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase($"mf-window-{Guid.NewGuid():N}").Options);
            var history = new MutualFundHistory(new MutualFundStore(db), new Factory(handler ?? failing!), settings, status,
                Options.Create(new MutualFundsOptions { RequestPaceSeconds = 0 }), NullLogger<MutualFundHistory>.Instance);

            var rig = new Rig { History = history, Status = status, Http = failing };
            history.Sleep = (span, _) =>
            {
                rig.Sleeps.Add(span);
                return Task.CompletedTask;
            };
            return rig;
        }
    }

    /// <summary>AMFI down: every request fails at once.</summary>
    private sealed class Failing : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        }
    }

    /// <summary>AMFI accepts the request and never answers.</summary>
    private sealed class Hanging : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool WasCut { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCut = true;
                throw;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class MemorySettings : IProcessSettingsStore
    {
        private readonly Dictionary<string, string> _values = new();

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.GetValueOrDefault(key));

        public Task SetAsync(string key, string value, string? updatedBy = null, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_values.Remove(key));
        public Task<int?> GetPidAsync(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPidAsync(string key, int processId, string? updatedBy = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteIfPidAsync(string key, int processId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
