using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Api.Services.Replay;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Contracts.OptionChain;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The AI Trader's loop: a brief, the model's plan, the rules' verdict, and a row for every look, "do nothing"
/// included; every ten minutes of the session, or on a market replay's clock.
/// </summary>
public class AiTraderAgentTests
{
    // Monday 5 Oct 2026, 11:00 IST.
    private static readonly DateTime Eleven = IstTime.FromIst(new DateTime(2026, 10, 5, 11, 0, 0));

    private const string BuyAtmCall = """
        {"action":"buy","underlying":"NIFTY","option":"CE","strike":"ATM","lots":1,"stopLoss":90,"target":160,
         "reason":"NIFTY above EMA20 and EMA50 with call OI falling at the call wall.","confidence":0.55}
        """;

    [Fact]
    public async Task A_buy_inside_the_rules_is_kept_as_allowed_with_its_contract_and_nothing_placed_in_shadow()
    {
        var (agent, ai, _) = Agent();
        ai.Provider.On(Judge1, Answer(BuyAtmCall));

        var row = await agent.DecideAsync(Eleven, AiTraderModes.Shadow, null, default);

        Assert.Equal((AiTraderPlan.Buy, "NIFTY", true, "ok", false), (row.Action, row.Underlying, row.Allowed, row.Rule, row.Executed));
        Assert.Contains("NSE:NIFTY26O0622650CE", row.ResultJson);
        Assert.Contains("MARKET BRIEF", row.Brief);
        Assert.Contains("YOUR BOOK", row.Brief);
        Assert.NotNull(row.CallId);
        Assert.Equal(1, await ai.Db.AiTraderDecisions.CountAsync());
        // The brief went to the model as the user's message.
        Assert.Contains("MARKET BRIEF", ai.Provider.Requests.Single(r => r.Model == Judge1).Body);
    }

    [Fact]
    public async Task A_plan_outside_the_rules_is_kept_as_refused_with_the_rule()
    {
        var (agent, ai, _) = Agent();
        ai.Provider.On(Judge1, Answer(BuyAtmCall.Replace("\"stopLoss\":90", "\"stopLoss\":40")));

        var row = await agent.DecideAsync(Eleven, AiTraderModes.Shadow, null, default);

        Assert.Equal((false, "stop"), (row.Allowed, row.Rule));
    }

    [Fact]
    public async Task The_next_look_reads_what_the_rules_refused_on_the_last_one()
    {
        var (agent, ai, _) = Agent();
        ai.Provider.On(Judge1, Answer(BuyAtmCall.Replace("\"stopLoss\":90", "\"stopLoss\":40")),
            Answer("""{"action":"none","reason":"Waiting.","confidence":0.3}"""));

        var first = await agent.DecideAsync(Eleven, AiTraderModes.Shadow, null, default);
        var second = await agent.DecideAsync(Eleven.AddMinutes(10), AiTraderModes.Shadow, null, default);

        Assert.Contains("YOUR LAST LOOKS: none yet today.", first.Brief);
        Assert.Contains("- 11:00 buy NIFTY ATM CE, 1 lot(s), stop 40, target 160 → refused (stop): The stop ₹40 is more than 40% below", second.Brief);
    }

    [Fact]
    public void A_last_look_allowed_in_shadow_says_it_was_not_placed() =>
        Assert.Contains("allowed, not placed (shadow mode)", AiTraderAgent.LastLooks([new AiTraderDecision
        {
            ClockUtc = Eleven, Mode = AiTraderModes.Shadow, Action = AiTraderPlan.Buy, Underlying = "NIFTY", Allowed = true, Rule = "ok",
            PlanJson = """{"action":"buy","underlying":"NIFTY","option":"PE","strike":"ATM","lots":2,"stopLoss":60,"target":140}""",
        }]));

    [Fact]
    public async Task An_answer_that_is_not_a_plan_is_kept_as_unreadable_with_its_text()
    {
        var (agent, ai, _) = Agent();
        ai.Provider.On(Judge1, Answer("I think the market looks bullish."));

        var row = await agent.DecideAsync(Eleven, AiTraderModes.Shadow, null, default);

        Assert.Equal(("unreadable", false), (row.Rule, row.Allowed));
        Assert.Contains("bullish", row.PlanJson);
    }

    [Fact]
    public async Task It_looks_every_ten_minutes_of_the_session_and_never_outside_it()
    {
        var (agent, ai, _) = Agent();
        ai.Provider.On(Judge1, Answer("""{"action":"none","reason":"Range-bound.","confidence":0.4}"""),
            Answer("""{"action":"none","reason":"Still range-bound.","confidence":0.4}"""));

        Assert.False(await agent.RunOnceAsync(IstTime.FromIst(new DateTime(2026, 10, 5, 9, 10, 0)), default));
        Assert.True(await agent.RunOnceAsync(Eleven, default));
        Assert.False(await agent.RunOnceAsync(Eleven.AddMinutes(5), default));
        Assert.True(await agent.RunOnceAsync(Eleven.AddMinutes(10), default));
        Assert.False(await agent.RunOnceAsync(IstTime.FromIst(new DateTime(2026, 10, 3, 11, 0, 0)), default));   // a Saturday

        Assert.Equal(new[] { AiTraderModes.Shadow, AiTraderModes.Shadow }, await ai.Db.AiTraderDecisions.Select(d => d.Mode).ToListAsync());
    }

    [Fact]
    public async Task In_a_replay_it_was_asked_into_it_decides_on_the_replays_clock()
    {
        var replayed = new DateOnly(2026, 9, 30);
        var book = new MarketReplayBook();
        book.Begin(replayed);
        book.Apply([new UpsertLiveTickRequest { Symbol = "NSE:NIFTY50-INDEX", LastTradedPrice = 22600m, ExchangeTimestampUtc = IstTime.FromIst(new DateTime(2026, 9, 30, 11, 0, 5)) }]);
        var session = new ReplaySessionState(4, "2026-09-30", 1, "09:15", MarketReplayService.StatePlaying, [], DateTime.UtcNow, null, null, "admin", AiTrader: true);
        var (agent, ai, briefs) = Agent(book, session);
        ai.Provider.On(Judge1, Answer("""{"action":"none","reason":"Waiting for the range to break.","confidence":0.3}"""));

        // The wall clock is a Saturday evening: only the replay's clock counts.
        Assert.True(await agent.RunOnceAsync(IstTime.FromIst(new DateTime(2026, 10, 3, 19, 0, 0)), default));
        Assert.False(await agent.RunOnceAsync(IstTime.FromIst(new DateTime(2026, 10, 3, 19, 1, 0)), default));

        var row = await ai.Db.AiTraderDecisions.SingleAsync();
        Assert.Equal((AiTraderModes.Replay, 4L, new DateOnly(2026, 9, 30)), (row.Mode, row.ReplaySessionId, row.Day));
        Assert.True(briefs.Replay);
    }

    [Fact]
    public void A_buy_resolves_to_the_strike_it_names_on_the_chains_grid_priced_at_the_ask()
    {
        var (agent, _, briefs) = Agent();
        var brief = briefs.Brief(Eleven);
        var plan = new AiTraderPlan(AiTraderPlan.Buy, "NIFTY", "PE", "ATM-1", 1, 50, 120, null, null, null, "x", 0.5);

        var contract = agent.Resolve(plan, brief, replay: false);

        Assert.Equal(("NSE:NIFTY26O0622600PE", 22600m, 81m, 65), (contract!.Symbol, contract.Strike, contract.Ask, contract.LotSize));
        Assert.Null(agent.Resolve(plan with { Strike = "ATM+5" }, brief, replay: false));   // not on the recorded chain
    }

    // ---------- helpers ----------

    private static (AiTraderAgent Agent, Services Ai, FakeBriefs Briefs) Agent(IMarketReplayBook? book = null, ReplaySessionState? session = null)
    {
        var ai = Build(Settings());
        ai.Store.SetAgentEnabledAsync(AiCatalog.AiTrader, true, "upendra", null).GetAwaiter().GetResult();
        var briefs = new FakeBriefs();
        var sessions = new MarketSessionService(new OpenCalendar());
        var agent = new AiTraderAgent(ai.Db, ai.Gateway, briefs, new FakeBooks(sessions), sessions, new FakeReplays(session), ai.Options,
            NullLogger<AiTraderAgent>.Instance, book);
        return (agent, ai, briefs);
    }

    private sealed class FakeBriefs : IAiTraderBriefs
    {
        public bool Replay { get; private set; }

        public MarketBrief Brief(DateTime asOf) => new(asOf, false, "MARKET BRIEF — test\nNIFTY 22,612 above EMA20 and EMA50.\n",
            new Dictionary<string, OptionChainResponse>(StringComparer.OrdinalIgnoreCase) { ["NIFTY"] = Chain() });

        public Task<MarketBrief> BuildAsync(DateTime asOfUtc, bool replay, CancellationToken cancellationToken)
        {
            Replay = replay;
            return Task.FromResult(Brief(asOfUtc) with { Replay = replay });
        }

        private static OptionChainResponse Chain()
        {
            OptionChainStrikeResponse Strike(decimal strike, decimal callAsk, decimal putAsk, bool atm = false) => new()
            {
                StrikePrice = strike, IsAtTheMoney = atm,
                Call = new OptionChainLegResponse { Symbol = $"NSE:NIFTY26O06{strike:0}CE", AskPrice = callAsk, BidPrice = callAsk - 0.5m, LastTradedPrice = callAsk - 0.2m },
                Put = new OptionChainLegResponse { Symbol = $"NSE:NIFTY26O06{strike:0}PE", AskPrice = putAsk, BidPrice = putAsk - 0.5m, LastTradedPrice = putAsk - 0.2m },
            };

            return new OptionChainResponse
            {
                Underlying = "NIFTY", ExpiryDate = new DateOnly(2026, 10, 6), AtTheMoneyStrike = 22650m, SpotPrice = 22612m,
                Header = new OptionChainHeaderResponse { LotSize = 65, AtTheMoneyStrike = 22650m },
                Strikes = [Strike(22600m, 140m, 81m), Strike(22650m, 120m, 98m, atm: true), Strike(22700m, 96m, 121m)],
            };
        }
    }

    private sealed class FakeBooks(IMarketSessionService sessions) : IAiTraderBooks
    {
        public Task<AiTraderBook> ReadAsync(DateTime clockUtc, bool replay, CancellationToken cancellationToken) =>
            Task.FromResult(new AiTraderBook(clockUtc, sessions.GetSessionInfo(clockUtc, "NSE", "FO").IsTradingDay || replay, false, 0m, 0, [], []));
    }

    private sealed class FakeReplays(ReplaySessionState? session) : IReplaySessions
    {
        public Task<ReplaySessionState?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(session);
    }

    private sealed class OpenCalendar : IMarketCalendar
    {
        public MarketHoliday? HolidayOn(string exchange, DateOnly date) => null;

        public MarketSpecialSession? SpecialSessionOn(string exchange, DateOnly date) => null;

        public bool HasYear(string exchange, int year) => true;

        public bool IsLoaded => true;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

/// <summary>The brief's arithmetic: 5-minute bars on the IST grid and the mean true range.</summary>
public class MarketBriefArithmeticTests
{
    private static LiveBarResponse Bar(DateTime start, decimal o, decimal h, decimal l, decimal c) =>
        new() { Symbol = "NSE:NIFTY50-INDEX", Resolution = "1m", BarStartUtc = start, Open = o, High = h, Low = l, Close = c };

    [Fact]
    public void Minutes_roll_into_five_minute_bars_starting_at_0915_ist()
    {
        var open = IstTime.FromIst(new DateTime(2026, 10, 5, 9, 15, 0));
        var minutes = Enumerable.Range(0, 7).Select(i => Bar(open.AddMinutes(i), 100 + i, 101 + i, 99 + i, 100.5m + i)).ToList();

        var fives = MarketBriefBuilder.FiveMinute(minutes);

        Assert.Equal(new[] { open, open.AddMinutes(5) }, fives.Select(b => b.BarStartUtc));
        Assert.Equal((100m, 105m, 99m, 104.5m), (fives[0].Open, fives[0].High, fives[0].Low, fives[0].Close));
        Assert.Equal((105m, 107m, 106.5m), (fives[1].Open, fives[1].High, fives[1].Close));
    }

    [Fact]
    public void The_atr_is_the_mean_true_range_counting_gaps_from_the_previous_close()
    {
        var t = IstTime.FromIst(new DateTime(2026, 10, 5, 9, 15, 0));
        var bars = new List<LiveBarResponse>
        {
            Bar(t, 100, 102, 98, 100),
            Bar(t.AddMinutes(5), 105, 106, 104, 105),   // gap up: true range 6 (106 − the previous close 100)
            Bar(t.AddMinutes(10), 105, 107, 103, 104),  // range 4
        };

        Assert.Equal(5.0, MarketBriefBuilder.Atr(bars, 2));
        Assert.Null(MarketBriefBuilder.Atr(bars, 3));
    }
}

public class MarketBriefWordingTests
{
    [Fact]
    public void A_range_forecast_reads_as_its_median_band_and_buckets()
    {
        var text = MarketBriefBuilder.Prediction("""{"median":0.8308,"low80":0.492,"high80":1.4029,"points":{"median":187.93},"buckets":{"quiet":0.3803,"normal":0.3789,"wild":0.2408}}""");
        Assert.Equal("median move 0.83% (≈188 pts), 80% band 0.49–1.40%; quiet 38%, normal 38%, wild 24%", text);
    }

    [Fact]
    public void Any_other_forecast_reads_as_its_json() =>
        Assert.Equal("""{"up":0.55,"down":0.45}""", MarketBriefBuilder.Prediction("""{ "up": 0.55, "down": 0.45 }"""));
}

public class AiTraderSummaryTests
{
    [Theory]
    [InlineData("""{"action":"buy","underlying":"NIFTY","option":"PE"}""", "PE")]
    [InlineData("""{"action":"none","option":null}""", null)]
    [InlineData("""{"answer":"not a plan"}""", null)]
    [InlineData("", null)]
    [InlineData("{cut", null)]
    public void A_list_row_names_the_option_its_plan_names(string planJson, string? option) =>
        Assert.Equal(option, AlgoTrading.Api.Controllers.AiTraderController.OptionOf(planJson));
}
