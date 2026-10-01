using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Infrastructure.Services;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The AI Trader's rules (owner, 1 Oct 2026), one test each: the model only proposes, and the code refuses
/// anything outside them, naming the rule.
/// </summary>
public class AiTraderGuardTests
{
    private static readonly AiTraderRules Rules = new();

    // Monday 5 Oct 2026, 11:00 IST.
    private static readonly DateTime Eleven = IstTime.FromIst(new DateTime(2026, 10, 5, 11, 0, 0));

    private static readonly AiTraderContract Nifty22650Ce =
        new("NSE:NIFTY26O0622650CE", "NIFTY", "CE", 22650m, new DateOnly(2026, 10, 6), Ask: 120m, LotSize: 65);

    private static AiTraderPlan Buy(int? lots = 1, decimal? stop = 90m, decimal? target = 160m, string underlying = "NIFTY", string option = "CE") =>
        new(AiTraderPlan.Buy, underlying, option, "ATM", lots, stop, target, null, null, null, "Spot broke the morning high on rising call OI.", 0.6);

    private static AiTraderBook Book(DateTime? clock = null, bool tradingDay = true, bool kill = false, decimal net = 0m, int opened = 0,
        IReadOnlyList<AiTraderOpenPosition>? open = null, IReadOnlyList<AiTraderRun>? runs = null) =>
        new(clock ?? Eleven, tradingDay, kill, net, opened, open ?? [], runs ?? []);

    private static AiTraderOpenPosition Position(long id, decimal premium) =>
        new(id, $"NSE:NIFTY26O06{22000 + id}CE", 1, premium / 65m, premium, null, null, null, 0m);

    private static void Refused(string rule, AiTraderVerdict verdict)
    {
        Assert.False(verdict.Allowed, verdict.Why);
        Assert.Equal(rule, verdict.Rule);
    }

    [Fact]
    public void A_buy_inside_every_rule_is_allowed_and_says_what_it_will_do()
    {
        var verdict = AiTraderGuard.Check(Buy(), Book(), Rules, Nifty22650Ce);

        Assert.True(verdict.Allowed, verdict.Why);
        Assert.Contains("NSE:NIFTY26O0622650CE", verdict.Why);
        Assert.Contains("₹7,800", verdict.Why);   // 1 lot × 65 × ₹120
    }

    [Fact]
    public void Nothing_is_placed_while_the_kill_switch_is_on() =>
        Refused("kill-switch", AiTraderGuard.Check(Buy(), Book(kill: true), Rules, Nifty22650Ce));

    [Fact]
    public void The_kill_switch_never_stops_it_closing_its_own_position_or_stopping_its_own_run()
    {
        // The desk's rule (RiskManagementService): halting means "open nothing more"; getting flat is never refused.
        var book = Book(kill: true, open: [Position(41, 5_000m)], runs: [new(7, "Ghost", "NIFTY", 0)]);
        var exit = new AiTraderPlan(AiTraderPlan.Exit, null, null, null, null, null, null, null, 41, null, "Kill switch is on: get flat.", 0.9);
        var stop = new AiTraderPlan(AiTraderPlan.StopStrategy, null, null, null, null, null, null, null, null, 7, "Kill switch is on.", 0.9);
        var start = new AiTraderPlan(AiTraderPlan.StartStrategy, "NIFTY", null, null, null, null, null, "ChainFlowBuy", null, null, "Trend day.", 0.5);

        Assert.True(AiTraderGuard.Check(exit, book, Rules).Allowed);
        Assert.True(AiTraderGuard.Check(stop, book, Rules).Allowed);
        Refused("kill-switch", AiTraderGuard.Check(start, book, Rules));
    }

    [Theory]
    [InlineData(9, 19)]
    [InlineData(14, 46)]
    public void A_buy_outside_0920_to_1445_is_refused(int hour, int minute) =>
        Refused("hours", AiTraderGuard.Check(Buy(), Book(clock: IstTime.FromIst(new DateTime(2026, 10, 5, hour, minute, 0))), Rules, Nifty22650Ce));

    [Fact]
    public void A_buy_on_a_day_with_no_session_is_refused() =>
        Refused("hours", AiTraderGuard.Check(Buy(), Book(tradingDay: false), Rules, Nifty22650Ce));

    [Fact]
    public void At_ten_thousand_down_after_charges_nothing_new_opens_that_day()
    {
        Refused("daily-loss", AiTraderGuard.Check(Buy(), Book(net: -10_000m), Rules, Nifty22650Ce));
        Assert.True(AiTraderGuard.Check(Buy(), Book(net: -9_999m), Rules, Nifty22650Ce).Allowed);
    }

    [Fact]
    public void The_eleventh_trade_of_a_day_is_refused() =>
        Refused("trades-a-day", AiTraderGuard.Check(Buy(), Book(opened: 10), Rules, Nifty22650Ce));

    [Theory]
    [InlineData("FINNIFTY", "CE")]
    [InlineData("NIFTY", "FUT")]
    public void Only_nifty_banknifty_and_sensex_calls_and_puts(string underlying, string option) =>
        Refused("instrument", AiTraderGuard.Check(Buy(underlying: underlying, option: option), Book(), Rules, Nifty22650Ce));

    [Fact]
    public void A_buy_with_no_priced_contract_is_refused() =>
        Refused("contract", AiTraderGuard.Check(Buy(), Book(), Rules, contract: null));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void At_most_two_lots_a_trade(int lots) =>
        Refused("size", AiTraderGuard.Check(Buy(lots: lots), Book(), Rules, Nifty22650Ce));

    [Fact]
    public void At_most_fifty_thousand_of_premium_a_trade()
    {
        var dear = Nifty22650Ce with { Ask = 400m };   // 2 × 65 × 400 = ₹52,000
        Refused("size", AiTraderGuard.Check(Buy(lots: 2, stop: 300m, target: 500m), Book(), Rules, dear));
    }

    [Fact]
    public void At_most_three_positions_open_at_once() =>
        Refused("open-positions", AiTraderGuard.Check(Buy(), Book(open: [Position(1, 5_000m), Position(2, 5_000m), Position(3, 5_000m)]), Rules, Nifty22650Ce));

    [Fact]
    public void The_premium_in_use_never_passes_the_accounts_five_lakh()
    {
        var rules = new AiTraderRules { Capital = 20_000m };
        Refused("capital", AiTraderGuard.Check(Buy(), Book(open: [Position(1, 15_000m)]), rules, Nifty22650Ce));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(120.0)]     // at the entry
    [InlineData(70.0)]      // more than 40% below ₹120 (the lowest is ₹72)
    public void Every_buy_needs_a_stop_below_the_entry_and_no_more_than_forty_percent_below(double? stop) =>
        Refused("stop", AiTraderGuard.Check(Buy(stop: stop is null ? null : (decimal)stop), Book(), Rules, Nifty22650Ce));

    [Theory]
    [InlineData(null)]
    [InlineData(120.0)]
    public void Every_buy_needs_a_target_above_the_entry(double? target) =>
        Refused("target", AiTraderGuard.Check(Buy(target: target is null ? null : (decimal)target), Book(), Rules, Nifty22650Ce));

    [Fact]
    public void It_may_close_only_its_own_positions_and_at_any_hour()
    {
        var book = Book(clock: IstTime.FromIst(new DateTime(2026, 10, 5, 15, 10, 0)), net: -12_000m, open: [Position(41, 5_000m)]);
        var mine = new AiTraderPlan(AiTraderPlan.Exit, null, null, null, null, null, null, null, 41, null, "Target near.", 0.7);

        Assert.True(AiTraderGuard.Check(mine, book, Rules).Allowed);
        Refused("own-book", AiTraderGuard.Check(mine with { PositionId = 99 }, book, Rules));
    }

    [Fact]
    public void An_exit_naming_no_position_closes_the_only_open_one_on_its_underlying_and_option()
    {
        // Replay #4 of 30 Sep, 13:56: the model named the contract, not the position, while it held one NIFTY CE.
        var book = Book(open: [Open(2, "NSE:NIFTY26O0622700CE"), Open(3, "NSE:BANKNIFTY26O2851500CE"), Open(4, "NSE:NIFTY26O0622500PE")]);
        var exit = new AiTraderPlan(AiTraderPlan.Exit, "NIFTY", "CE", "ATM-1", null, null, null, null, null, null, "The move stalled.", 0.5);

        var verdict = AiTraderGuard.Check(exit, book, Rules);

        Assert.True(verdict.Allowed, verdict.Why);
        Assert.Equal(2L, verdict.PositionId);
        Assert.Equal(3L, AiTraderGuard.Check(exit with { Underlying = "BANKNIFTY" }, book, Rules).PositionId);
        Assert.Equal(4L, AiTraderGuard.Check(exit with { Option = "PE" }, book, Rules).PositionId);
        // Named, the position it names.
        Assert.Equal(4L, AiTraderGuard.Check(exit with { PositionId = 4 }, book, Rules).PositionId);
    }

    [Fact]
    public void An_exit_naming_no_position_is_refused_when_none_or_several_match_and_says_which_are_open()
    {
        var book = Book(open: [Open(2, "NSE:NIFTY26O0622700CE"), Open(5, "NSE:NIFTY26O0622750CE"), Open(6, "NSE:NIFTYNXT5026O0668000CE")]);
        var exit = new AiTraderPlan(AiTraderPlan.Exit, "NIFTY", "CE", null, null, null, null, null, null, null, "Out.", 0.5);

        var several = AiTraderGuard.Check(exit, book, Rules);
        Refused("own-book", several);
        Assert.Contains("2 (NSE:NIFTY26O0622700CE)", several.Why);
        Assert.Contains("5 (NSE:NIFTY26O0622750CE)", several.Why);

        // NIFTY's prefix is not NIFTYNXT50's, nor FINNIFTY's: none of these is a NIFTY PE.
        Refused("own-book", AiTraderGuard.Check(exit with { Option = "PE" }, book, Rules));
        Refused("own-book", AiTraderGuard.Check(exit with { Underlying = "FINNIFTY" }, book, Rules));
        Refused("own-book", AiTraderGuard.Check(exit with { Underlying = null, Option = null }, book, Rules));
        // An underlying alone, with one match, is enough.
        Assert.Equal(6L, AiTraderGuard.Check(exit with { Underlying = "NIFTYNXT50", Option = null }, book, Rules).PositionId);
    }

    private static AiTraderOpenPosition Open(long id, string symbol) => new(id, symbol, 1, 100m, 6_500m, null, 70m, 150m, 0m);

    [Fact]
    public void It_may_start_only_listed_strategies_up_to_three_runs_and_stop_only_its_own()
    {
        var start = new AiTraderPlan(AiTraderPlan.StartStrategy, "NIFTY", null, null, null, null, null, "ChainFlowBuy", null, null, "Trend day.", 0.5);
        Assert.True(AiTraderGuard.Check(start, Book(), Rules).Allowed);
        Refused("strategy-list", AiTraderGuard.Check(start with { Strategy = "Fulcrum" }, Book(), Rules));
        Refused("strategy-runs", AiTraderGuard.Check(start, Book(runs: [new(1, "Ghost", "NIFTY", 0), new(2, "Ghost", "SENSEX", 0), new(3, "ChainFlowBuy", "BANKNIFTY", 0)]), Rules));
        Refused("daily-loss", AiTraderGuard.Check(start, Book(net: -10_500m), Rules));

        var stop = new AiTraderPlan(AiTraderPlan.StopStrategy, null, null, null, null, null, null, null, null, 7, "Chop.", 0.5);
        Assert.True(AiTraderGuard.Check(stop, Book(runs: [new(7, "Ghost", "NIFTY", 0)]), Rules).Allowed);
        Refused("own-runs", AiTraderGuard.Check(stop, Book(runs: [new(8, "Ghost", "NIFTY", 0)]), Rules));
    }

    [Fact]
    public void An_action_it_does_not_know_is_refused_and_none_is_always_fine()
    {
        var odd = new AiTraderPlan("sell", "NIFTY", "CE", "ATM", 1, null, null, null, null, null, "Short it.", 0.9);
        Refused("action", AiTraderGuard.Check(odd, Book(), Rules));
        Assert.True(AiTraderGuard.Check(odd with { Action = AiTraderPlan.None }, Book(net: -50_000m, tradingDay: false), Rules).Allowed);
    }
}
