using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Api.Services.Replay;
using AlgoTrading.Application.Risk;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The shadow book: in shadow and replay an allowed buy is kept as if placed, bought at the ask, checked every
/// minute at the bid against its stop and target, squared off at the close, and scored after charges.
/// </summary>
public class AiTraderShadowBookTests
{
    // Monday 5 Oct 2026, 11:00 IST.
    private static readonly DateTime Eleven = IstTime.FromIst(new DateTime(2026, 10, 5, 11, 0, 0));

    private const string Symbol = "NSE:NIFTY26O0622650CE";   // the test chain's ATM call: ask 120, lot 65

    private const string BuyAtmCall = """
        {"action":"buy","underlying":"NIFTY","option":"CE","strike":"ATM","lots":1,"stopLoss":90,"target":160,
         "reason":"NIFTY above EMA20 and EMA50.","confidence":0.55}
        """;

    private const string Hold = """{"action":"none","reason":"Holding.","confidence":0.4}""";

    [Fact]
    public async Task An_allowed_buy_opens_at_the_ask_and_the_next_look_sees_it_marked()
    {
        var (agent, ai, _, quotes) = AiTraderAgentTests.Agent();
        ai.Provider.On(Judge1, Answer(BuyAtmCall), Answer(Hold));

        var first = await agent.DecideAsync(Eleven, AiTraderModes.Shadow, null, default);
        var p = await ai.Db.AiTraderShadowPositions.AsNoTracking().SingleAsync();
        Assert.Equal((first.Id, Symbol, 120m, 1, 65, 90m, 160m, (DateTime?)null),
            (p.DecisionId, p.Symbol, p.EntryPrice, p.Lots, p.LotSize, p.StopLoss, p.Target, p.ExitUtc));
        Assert.Contains($"\"shadowPositionId\":{p.Id}", first.ResultJson);

        // A minute check between two looks marks it; it decides nothing.
        quotes.Set(Symbol, bid: 130m, last: 130.5m);
        Assert.False(await agent.RunOnceAsync(Eleven.AddMinutes(5), default));
        var second = await agent.DecideAsync(Eleven.AddMinutes(10), AiTraderModes.Shadow, null, default);

        Assert.Contains("YOUR BOOK (shadow:", second.Brief);
        Assert.Contains("trades opened 1 of 10; positions 1 of 3", second.Brief);
        Assert.Contains($"- position {p.Id}: {Symbol} × 1 lot(s), entry 120, mark 130.5, P&L ₹682.5, stop 90, target 160", second.Brief);
        Assert.Contains("→ allowed, opened in your shadow book", second.Brief);
    }

    [Fact]
    public async Task The_minute_check_stops_it_at_the_bid_once_the_bid_is_at_or_under_the_stop()
    {
        var (agent, ai, _, quotes) = AiTraderAgentTests.Agent();
        ai.Provider.On(Judge1, Answer(BuyAtmCall));
        await agent.DecideAsync(Eleven, AiTraderModes.Shadow, null, default);

        quotes.Set(Symbol, bid: 91m, last: 91.5m);
        await agent.RunOnceAsync(Eleven.AddMinutes(1), default);
        Assert.Null((await ai.Db.AiTraderShadowPositions.AsNoTracking().SingleAsync()).ExitUtc);

        quotes.Set(Symbol, bid: 88m, last: 88.5m);
        await agent.RunOnceAsync(Eleven.AddMinutes(2), default);

        var p = await ai.Db.AiTraderShadowPositions.AsNoTracking().SingleAsync();
        decimal charges = OptionCharges.For(120m * 65, 88m * 65, 2, ChargeSchedule.IndexOptions).Total;
        Assert.Equal((AiTraderShadowBook.Stopped, 88m, Eleven.AddMinutes(2), charges, Math.Round((88m - 120m) * 65 - charges, 2)),
            (p.ExitReason, p.ExitPrice, p.ExitUtc, p.Charges, p.NetPnl));
    }

    [Fact]
    public async Task The_minute_check_takes_the_target_and_squares_off_at_the_close()
    {
        var (agent, ai, _, quotes) = AiTraderAgentTests.Agent();
        ai.Provider.On(Judge1, Answer(BuyAtmCall), Answer(BuyAtmCall.Replace("\"strike\":\"ATM\"", "\"strike\":\"ATM+1\"").Replace("\"stopLoss\":90", "\"stopLoss\":70").Replace("\"target\":160", "\"target\":130")));
        await agent.DecideAsync(Eleven, AiTraderModes.Shadow, null, default);
        await agent.DecideAsync(Eleven.AddMinutes(10), AiTraderModes.Shadow, null, default);

        quotes.Set(Symbol, bid: 160m, last: 160.4m);
        quotes.Set("NSE:NIFTY26O0622700CE", bid: 100m, last: 100.2m);
        await agent.RunOnceAsync(Eleven.AddMinutes(11), default);
        await agent.RunOnceAsync(IstTime.FromIst(new DateTime(2026, 10, 5, 15, 30, 0)), default);

        var rows = await ai.Db.AiTraderShadowPositions.AsNoTracking().OrderBy(p => p.Id).ToListAsync();
        Assert.Equal(new[] { (AiTraderShadowBook.TargetHit, (decimal?)160m), (AiTraderShadowBook.SessionClose, (decimal?)100m) },
            rows.Select(p => (p.ExitReason, p.ExitPrice)));
        Assert.Equal(rows.Sum(p => p.NetPnl ?? 0m), AiTraderShadowBook.Net(rows));
    }

    [Fact]
    public async Task Its_own_exit_closes_it_at_the_bid()
    {
        var (agent, ai, _, quotes) = AiTraderAgentTests.Agent();
        ai.Provider.On(Judge1, Answer(BuyAtmCall), Answer("""{"action":"exit","positionId":1,"reason":"The move stalled.","confidence":0.5}"""));
        await agent.DecideAsync(Eleven, AiTraderModes.Shadow, null, default);
        Assert.Equal(1, (await ai.Db.AiTraderShadowPositions.AsNoTracking().SingleAsync()).Id);

        quotes.Set(Symbol, bid: 125m, last: 125.5m);
        var exit = await agent.DecideAsync(Eleven.AddMinutes(10), AiTraderModes.Shadow, null, default);

        var p = await ai.Db.AiTraderShadowPositions.AsNoTracking().SingleAsync();
        Assert.Equal((true, "ok", AiTraderShadowBook.ExitedByIt, (decimal?)125m), (exit.Allowed, exit.Rule, p.ExitReason, p.ExitPrice));
        Assert.Contains("\"netPnl\":", exit.ResultJson);
    }

    [Fact]
    public async Task An_exit_naming_the_contract_but_no_position_closes_its_only_matching_position()
    {
        var (agent, ai, _, quotes) = AiTraderAgentTests.Agent();
        ai.Provider.On(Judge1, Answer(BuyAtmCall),
            Answer("""{"action":"exit","underlying":"NIFTY","option":"CE","strike":"ATM-1","positionId":null,"reason":"The move stalled.","confidence":0.5}"""));
        await agent.DecideAsync(Eleven, AiTraderModes.Shadow, null, default);

        quotes.Set(Symbol, bid: 125m, last: 125.5m);
        var exit = await agent.DecideAsync(Eleven.AddMinutes(10), AiTraderModes.Shadow, null, default);

        var p = await ai.Db.AiTraderShadowPositions.AsNoTracking().SingleAsync();
        Assert.Equal((true, "ok", AiTraderShadowBook.ExitedByIt, (decimal?)125m), (exit.Allowed, exit.Rule, p.ExitReason, p.ExitPrice));
        Assert.Contains($"\"shadowPositionId\":{p.Id}", exit.ResultJson);
    }

    [Fact]
    public async Task A_position_left_from_an_earlier_day_closes_at_its_last_mark()
    {
        var (agent, ai, _, _) = AiTraderAgentTests.Agent();
        ai.Db.AiTraderShadowPositions.Add(Position(day: new DateOnly(2026, 10, 5), mark: 111m));
        await ai.Db.SaveChangesAsync();

        await agent.RunOnceAsync(IstTime.FromIst(new DateTime(2026, 10, 6, 9, 0, 0)), default);

        var p = await ai.Db.AiTraderShadowPositions.AsNoTracking().SingleAsync();
        Assert.Equal((AiTraderShadowBook.SessionClose, (decimal?)111m), (p.ExitReason, p.ExitPrice));
    }

    [Fact]
    public async Task An_ended_replays_positions_close_and_a_playing_replays_stay_open()
    {
        var session = new ReplaySessionState(9, "2026-09-30", 1, "09:15", MarketReplayService.StatePlaying, [], DateTime.UtcNow, null, null, "admin", AiTrader: true);
        var (agent, ai, _, _) = AiTraderAgentTests.Agent(session: session);
        var ended = Position(day: new DateOnly(2026, 9, 29), mark: 104m);
        ended.Mode = AiTraderModes.Replay;
        ended.ReplaySessionId = 8;
        var playing = Position(day: new DateOnly(2026, 9, 30), mark: 99m);
        playing.Mode = AiTraderModes.Replay;
        playing.ReplaySessionId = 9;
        ai.Db.AiTraderShadowPositions.AddRange(ended, playing);
        await ai.Db.SaveChangesAsync();

        await agent.RunOnceAsync(IstTime.FromIst(new DateTime(2026, 10, 3, 19, 0, 0)), default);

        var rows = await ai.Db.AiTraderShadowPositions.AsNoTracking().OrderBy(p => p.ReplaySessionId).ToListAsync();
        Assert.Equal(new[] { (AiTraderShadowBook.ReplayEnded, (decimal?)104m), (string.Empty, (decimal?)null) },
            rows.Select(p => (p.ExitReason, p.ExitPrice)));
    }

    [Fact]
    public async Task A_chain_capture_hours_old_is_no_price_now_but_the_sessions_last_one_prices_the_close()
    {
        var ai = Build(Settings());
        var day = new DateOnly(2026, 9, 30);
        DateTime Ist(int h, int m) => IstTime.FromIst(day.ToDateTime(new TimeOnly(h, m)));
        // The recorder's last capture of the day was at 11:00 (it stopped), and the feed carries no quote for the contract.
        MarketBriefBuilderTests.Chain(ai.Db, Ist(11, 0), expiry: new DateOnly(2026, 10, 6));
        var quotes = new AiTraderQuotes(ai.Db, new OptionChainService(ai.Db), Microsoft.Extensions.Logging.Abstractions.NullLogger<AiTraderQuotes>.Instance);
        var p = Position(day, mark: 112m);

        Assert.Equal(Ist(11, 0), (await quotes.QuoteAsync(p, Ist(11, 2), replay: false, default))?.UpdatedUtc);
        // At 13:00 the 11:00 capture is two hours old: not a price that can stop, take or mark it now.
        Assert.Null(await quotes.QuoteAsync(p, Ist(13, 0), replay: false, default));
        Assert.Null(await quotes.QuoteAsync(p, Ist(13, 0), replay: true, default));

        // After the close, the session's last capture is its closing price.
        MarketBriefBuilderTests.Chain(ai.Db, Ist(15, 29), expiry: new DateOnly(2026, 10, 6));
        Assert.Equal(Ist(15, 29), (await quotes.QuoteAsync(p, Ist(16, 10), replay: false, default))?.UpdatedUtc);
    }

    [Fact]
    public void An_open_positions_net_is_as_if_sold_at_its_mark_after_charges()
    {
        var p = Position(day: new DateOnly(2026, 10, 5), mark: 130m);
        decimal charges = OptionCharges.For(120m * 65, 130m * 65, 2, ChargeSchedule.IndexOptions).Total;
        Assert.Equal(Math.Round(10m * 65 - charges, 2), AiTraderShadowBook.Net([p]));
    }

    private static AiTraderShadowPosition Position(DateOnly day, decimal mark) => new()
    {
        CreatedUtc = DateTime.UtcNow, DecisionId = 1, Mode = AiTraderModes.Shadow, Day = day, Symbol = Symbol, Underlying = "NIFTY",
        OptionType = "CE", Strike = 22650m, Expiry = new DateOnly(2026, 10, 6), Lots = 1, LotSize = 65,
        EntryUtc = IstTime.FromIst(day.ToDateTime(new TimeOnly(11, 0))), EntryPrice = 120m, StopLoss = 90m, Target = 160m,
        MarkPrice = mark, MarkUtc = IstTime.FromIst(day.ToDateTime(new TimeOnly(15, 0))),
    };
}
