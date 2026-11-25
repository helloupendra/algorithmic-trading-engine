using AlgoTrading.Api.Services;
using AlgoTrading.Contracts.Strategies;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The overall profit lock (owner, 25 Nov 2026): ₹4,000 stop; once the day's P&amp;L reaches ₹5,000 keep
/// ₹3,000, at ₹10,000 keep ₹7,000, at ₹20,000 keep ₹15,000. A trip ends the run for the day.
/// </summary>
public class ProfitLockTests
{
    private static OverallRiskDto Ladder() => new()
    {
        StopLoss = 4_000m,
        LockSteps = new()
        {
            new ProfitLockStepDto { At = 5_000m, Keep = 3_000m },
            new ProfitLockStepDto { At = 10_000m, Keep = 7_000m },
            new ProfitLockStepDto { At = 20_000m, Keep = 15_000m },
        },
    };

    /// <summary>Feeds the sweeps in order; returns the index and reason of the first trip, or (-1, null).</summary>
    private static (int, string?) Sweep(OverallRiskDto rules, params decimal[] pnls)
    {
        var state = new RiskTrailState();
        for (int i = 0; i < pnls.Length; i++)
        {
            var reason = StrategyRiskGuardService.EvaluateOverall(pnls[i], rules, state);
            if (reason is not null) return (i, reason);
        }
        return (-1, null);
    }

    [Fact]
    public void Swings_below_the_first_step_trip_nothing_until_the_stop()
    {
        Assert.Equal((-1, (string?)null), Sweep(Ladder(), -100m, 156m, 4_999m, -3_999m, 2_000m));
        var (at, reason) = Sweep(Ladder(), -100m, 156m, -4_000m);
        Assert.Equal(2, at);
        Assert.StartsWith("Stop loss hit", reason);
    }

    [Fact]
    public void Five_thousand_keeps_three_thousand()
    {
        var (at, reason) = Sweep(Ladder(), 2_000m, 5_000m, 4_000m, 3_100m, 3_000m);
        Assert.Equal(4, at);
        Assert.StartsWith("Profit lock hit: P&L ₹3,000 fell to the ₹3,000 kept once it reached ₹5,000", reason);
    }

    [Fact]
    public void The_floor_rises_with_the_best_and_never_falls_back()
    {
        // 25 Nov's TrendRider call by hand: best +15,788 at 11:19, then down through +7,000 at 12:10.
        var (at, reason) = Sweep(Ladder(), 5_000m, 15_788m, 9_000m, 7_200m, 6_990m, 3_500m);
        Assert.Equal(4, at);
        Assert.Contains("kept once it reached ₹10,000 (best ₹15,788)", reason);
        Assert.Equal(3, Sweep(Ladder(), 21_000m, 18_000m, 16_000m, 15_000m).Item1);
    }

    [Fact]
    public void The_ladder_alone_is_a_rule_and_its_steps_must_rise()
    {
        var rules = new RiskRulesDto { Overall = new OverallRiskDto { LockSteps = Ladder().LockSteps } };
        Assert.True(rules.HasAnyRule);
        Assert.True(RiskRulesDto.TryValidate(rules, out _));
        Assert.Contains("overall lock ₹5,000 keeps ₹3,000 / ₹10,000 keeps ₹7,000 / ₹20,000 keeps ₹15,000", rules.Describe());

        var keepsTooMuch = new RiskRulesDto { Overall = new OverallRiskDto { LockSteps = new() { new() { At = 5_000m, Keep = 5_000m } } } };
        Assert.False(RiskRulesDto.TryValidate(keepsTooMuch, out var e1));
        Assert.Contains("lockSteps[0]", e1);
        var outOfOrder = new RiskRulesDto
        {
            Overall = new OverallRiskDto { LockSteps = new() { new() { At = 10_000m, Keep = 7_000m }, new() { At = 5_000m, Keep = 3_000m } } },
        };
        Assert.False(RiskRulesDto.TryValidate(outOfOrder, out var e2));
        Assert.Contains("lockSteps[1]", e2);
    }

    [Fact]
    public void A_changed_ladder_counts_as_a_trailing_change_and_survives_normalize()
    {
        var a = new RiskRulesDto { Overall = Ladder() };
        var b = RiskRulesDto.Normalize(a);
        Assert.Equal(3, b.Overall!.LockSteps!.Count);
        Assert.True(RiskRulesDto.SameTrailing(a, b));
        b.Overall.LockSteps[0].Keep = 2_500m;
        Assert.False(RiskRulesDto.SameTrailing(a, b));
    }
}
