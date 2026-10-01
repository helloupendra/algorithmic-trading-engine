using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Application.Risk;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The bar the AI Trader has to clear: a fixed 11:00 trend rule scored from the recorded ticks with the shadow
/// book's fills and charges, and the scoreboard that sets the two side by side.
/// </summary>
public class AiTraderBaselineTests
{
    private static readonly DateOnly Day = new(2026, 9, 30);
    private static readonly DateTime Eleven = IstTime.FromIst(Day.ToDateTime(new TimeOnly(11, 0)));
    private const string Call = "NSE:NIFTY26O0622700CE";

    // ---------- the rule ----------

    [Theory]
    [InlineData(1, "CE")]
    [InlineData(-1, "PE")]
    public void The_side_follows_the_trend_of_the_five_minute_bars_before_eleven(int slope, string expected)
    {
        var (option, why) = AiTraderBaselineScorer.Direction(Minutes(slope), Eleven);

        Assert.Equal(expected, option);
        Assert.Contains("EMA20", why);
    }

    [Fact]
    public void A_flat_market_or_too_few_bars_is_no_trade_with_the_reason()
    {
        Assert.Null(AiTraderBaselineScorer.Direction(Minutes(0), Eleven).Option);
        var (option, why) = AiTraderBaselineScorer.Direction(Minutes(1).TakeLast(100).ToList(), Eleven);
        Assert.Null(option);
        Assert.StartsWith("Only 20 five-minute bars", why);
    }

    [Fact]
    public void A_bar_that_began_at_eleven_or_later_is_never_read()
    {
        var minutes = Minutes(1).ToList();
        // A crash in the 11:00 minute must not change an 11:00 decision.
        minutes.Add(new LiveBarResponse { Symbol = "NSE:NIFTY50-INDEX", Resolution = "1m", BarStartUtc = Eleven, Open = 1, High = 1, Low = 1, Close = 1 });

        Assert.Equal("CE", AiTraderBaselineScorer.Direction(minutes, Eleven).Option);
    }

    [Fact]
    public void The_walk_exits_at_the_first_minute_whose_bid_crosses_the_stop_or_target_else_at_the_close()
    {
        BaselineTick T(int minute, decimal bid) => new(Eleven.AddMinutes(minute), bid + 0.2m, bid, bid + 0.5m);

        Assert.Equal((Eleven.AddMinutes(3), 69m, "stop"), AiTraderBaselineScorer.Walk([T(1, 90), T(2, 75), T(3, 69), T(4, 160)], 70m, 150m));
        Assert.Equal((Eleven.AddMinutes(2), 151m, "target"), AiTraderBaselineScorer.Walk([T(1, 120), T(2, 151)], 70m, 150m));
        Assert.Equal((Eleven.AddMinutes(2), 110m, "close"), AiTraderBaselineScorer.Walk([T(1, 120), T(2, 110)], 70m, 150m));
        Assert.Null(AiTraderBaselineScorer.Walk([], 70m, 150m));
    }

    [Fact]
    public async Task A_day_is_scored_once_from_its_recorded_ticks_and_kept()
    {
        var ai = Build(Settings());
        var market = new FakeMarket(Minutes(1));
        ai.Db.LiveTicks.AddRange(
            TickRow(Eleven.AddSeconds(5), last: 99.8m, bid: 99.5m, ask: 100m),
            TickRow(Eleven.AddMinutes(5), last: 130m, bid: 129.5m, ask: 130.5m),
            TickRow(Eleven.AddMinutes(30), last: 151m, bid: 150.5m, ask: 151.5m),
            TickRow(Eleven.AddMinutes(40), last: 90m, bid: 89.5m, ask: 90.5m));
        await ai.Db.SaveChangesAsync();
        var scorer = new AiTraderBaselineScorer(ai.Db, market, new MarketSessionService(new MarketReplayTests.OpenCalendar()));
        var later = IstTime.FromIst(new DateTime(2026, 10, 3, 12, 0, 0));

        var row = (await scorer.ForDayAsync(Day, later, default))!;

        decimal charges = OptionCharges.For(100m * 65, 150.5m * 65, 2, ChargeSchedule.IndexOptions).Total;
        Assert.Equal(("CE", Call, 100m, 70m, 150m, "target", 150.5m, Math.Round(50.5m * 65 - charges, 2)),
            (row.OptionType, row.Symbol, row.EntryPrice, row.StopLoss, row.Target, row.ExitReason, row.ExitPrice, row.NetPnl));
        Assert.Equal(1, market.ChainCalls);

        await scorer.ForDayAsync(Day, later, default);
        Assert.Equal(1, market.ChainCalls);
        Assert.Null(await scorer.ForDayAsync(new DateOnly(2026, 10, 3), later, default));
    }

    [Fact]
    public async Task A_day_without_a_trend_is_kept_as_no_trade_at_zero()
    {
        var ai = Build(Settings());
        var scorer = new AiTraderBaselineScorer(ai.Db, new FakeMarket(Minutes(0)), new MarketSessionService(new MarketReplayTests.OpenCalendar()));

        var row = (await scorer.ForDayAsync(Day, IstTime.FromIst(new DateTime(2026, 10, 3, 12, 0, 0)), default))!;

        Assert.Equal((string.Empty, 0m), (row.OptionType, row.NetPnl));
        Assert.Contains("no clear trend", row.Note);
        Assert.Equal(1, await ai.Db.AiTraderBaselines.CountAsync());
    }

    // ---------- the scoreboard ----------

    [Fact]
    public async Task The_scoreboard_sets_each_full_day_against_the_baseline_and_totals_only_those()
    {
        var ai = Build(Settings());
        Look(ai, 7, 9, 20); Look(ai, 7, 15, 0);
        Look(ai, 8, 11, 5); Look(ai, 8, 12, 0);   // started at 11:00: listed, not in the totals
        ai.Db.AiTraderShadowPositions.Add(new AiTraderShadowPosition
        {
            CreatedUtc = DateTime.UtcNow, Mode = AiTraderModes.Replay, ReplaySessionId = 7, Day = Day, Symbol = Call, Underlying = "NIFTY", OptionType = "CE",
            Lots = 1, LotSize = 65, EntryUtc = Eleven, EntryPrice = 100m, StopLoss = 70m, Target = 150m, ExitUtc = Eleven.AddHours(1), ExitPrice = 110m,
            ExitReason = "close", Charges = 50m, NetPnl = 600m,
        });
        ai.Db.AiTraderBaselines.Add(new AiTraderBaseline { Day = Day, Rule = AiTraderBaselineScorer.TrendRule, Underlying = "NIFTY", OptionType = "PE", NetPnl = -200m });
        await ai.Db.SaveChangesAsync();
        var controller = new AiTraderController(ai.Db, ai.Store, ai.Options);

        var board = Assert.IsType<AiTraderScoreboard>(Assert.IsType<OkObjectResult>(await controller.Scoreboard()).Value);

        Assert.Equal(new long?[] { 8, 7 }, board.Rows.Select(r => r.ReplaySessionId));
        var full = board.Rows.Single(r => r.ReplaySessionId == 7);
        Assert.Equal((true, 2, 1, 600m, 800m), (full.Full, full.Looks, full.Positions, full.Net, full.VsBaseline));
        Assert.False(board.Rows.Single(r => r.ReplaySessionId == 8).Full);
        Assert.Equal((1, 600m, -200m, 1, 1, 0), (board.Totals.Days, board.Totals.AiNet, board.Totals.BaselineNet, board.Totals.AiBeatBaseline,
            board.Totals.AiPositiveDays, board.Totals.BaselinePositiveDays));
    }

    // ---------- helpers ----------

    /// <summary>300 minutes before 11:00 that rise (1), fall (-1) or stay flat (0).</summary>
    private static List<LiveBarResponse> Minutes(int slope) => Enumerable.Range(0, 300).Select(i =>
    {
        decimal close = 22500m + slope * i * 0.8m;
        return new LiveBarResponse { Symbol = "NSE:NIFTY50-INDEX", Resolution = "1m", BarStartUtc = Eleven.AddMinutes(i - 300),
            Open = close, High = close + 1, Low = close - 1, Close = close };
    }).ToList();

    private static LiveTick TickRow(DateTime at, decimal last, decimal bid, decimal ask) => new()
    {
        Symbol = Call, ReceivedUtc = at, LastTradedPrice = last, BidPrice = bid, AskPrice = ask, SourceKey = "dhan",
    };

    private static void Look(Services ai, long replay, int hour, int minute) => ai.Db.AiTraderDecisions.Add(new AiTraderDecision
    {
        CreatedUtc = DateTime.UtcNow, ClockUtc = IstTime.FromIst(Day.ToDateTime(new TimeOnly(hour, minute))), Day = Day, Mode = AiTraderModes.Replay,
        ReplaySessionId = replay, Action = AiTraderPlan.None, Rule = "ok", Allowed = true,
    });

    private sealed class FakeMarket(IReadOnlyList<LiveBarResponse> minutes) : IBaselineMarket
    {
        public int ChainCalls { get; private set; }

        public Task<IReadOnlyList<LiveBarResponse>> MinutesBeforeAsync(string symbol, DateTime beforeUtc, CancellationToken cancellationToken) =>
            Task.FromResult(minutes);

        public Task<BaselineContract?> AtTheMoneyAsync(string underlying, string optionType, DateTime asOfUtc, CancellationToken cancellationToken)
        {
            ChainCalls++;
            return Task.FromResult<BaselineContract?>(new BaselineContract(optionType == "CE" ? Call : Call[..^2] + "PE", 22700m, 65));
        }
    }
}
