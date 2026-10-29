using AlgoTrading.Api.Services;
using AlgoTrading.Contracts.Backtest;

namespace AlgoTrading.UnitTests;

/// <summary>
/// What a backtest's results view is built from: the thinned equity curve the
/// console dates the deepest fall from, and the charges a run is netted of.
/// </summary>
public class BacktestResultsTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 3, 45, 0, DateTimeKind.Utc);

    private static List<BacktestEquityPoint> Curve(Func<int, decimal> equityAt, int count)
        => Enumerable.Range(0, count)
            .Select(i => new BacktestEquityPoint { AtUtc = Start.AddMinutes(i), Equity = equityAt(i), Realized = 0m, Unrealized = 0m })
            .ToList();

    [Fact]
    public void ThinCurve_KeepsThePeakTroughAndRecoveryOfTheDeepestFall()
    {
        // 10,000 points thinned to 100 (a stride of 100). The fall starts at
        // 1,234 (an equal high after an earlier one at 1,000), bottoms at
        // 5,555 and is made good at 7,777 — none of them on the stride.
        decimal At(int i) => i switch
        {
            < 1_000 => 100_000m + i,
            < 1_234 => 101_000m - (i - 1_000) % 7,
            1_234 => 101_000m,
            < 5_555 => 101_000m - (i - 1_234) * 2m,
            5_555 => 90_000m,
            < 7_777 => 92_000m,
            _ => 101_000m + (i - 7_777)
        };
        var curve = Curve(At, 10_000);

        var thinned = BacktestRunViewBuilder.ThinCurve(curve, 100);

        var kept = thinned.Select(p => (int)(p.AtUtc - Start).TotalMinutes).ToHashSet();
        Assert.Contains(1_234, kept);       // the last point at the peak
        Assert.Contains(5_555, kept);       // the trough
        Assert.Contains(7_777, kept);       // the first point back at the peak
        Assert.Contains(9_999, kept);       // the last point
        Assert.True(thinned.Count <= 104);
        Assert.Equal(thinned.OrderBy(p => p.AtUtc).Select(p => p.AtUtc), thinned.Select(p => p.AtUtc));
    }

    [Fact]
    public void ThinCurve_KeepsNoRecoveryWhenTheRunEndedUnderThePeak()
    {
        var curve = Curve(i => i < 500 ? 100_000m + i : 99_000m - i % 3, 5_000);

        var thinned = BacktestRunViewBuilder.ThinCurve(curve, 50);

        Assert.Contains(thinned, p => p.AtUtc == Start.AddMinutes(499));
        Assert.DoesNotContain(thinned.SkipWhile(p => p.AtUtc <= Start.AddMinutes(499)), p => p.Equity >= 100_499m);
    }

    [Fact]
    public void ThinCurve_SendsAShortCurveWhole()
    {
        var curve = Curve(i => 100_000m - i, 10);
        Assert.Same(curve, BacktestRunViewBuilder.ThinCurve(curve, 2_000));
    }

    [Fact]
    public void RunCharges_FlatRateIsPerLotPerFill()
    {
        Assert.Equal(400m, BacktestRunViewBuilder.RunCharges(costModel: false, chargesPerLot: 40m, filledLots: 10, 200_000m, latest: null));
        Assert.Equal(0m, BacktestRunViewBuilder.RunCharges(costModel: false, chargesPerLot: 0m, filledLots: 10, 200_000m, latest: null));
    }

    [Fact]
    public void RunCharges_RealCostsAreWhatTheLatestSnapshotWasNettedOf()
    {
        // ₹2,00,000 capital, ₹5,000 realized, ₹0 open, balance ₹2,03,812.40:
        // the runner had charged ₹1,187.60 by then. A flat rate typed next to
        // a costs block is ignored, as the runner ignores it.
        var latest = new BacktestEquityPoint { AtUtc = Start, Equity = 203_812.40m, Realized = 5_000m, Unrealized = 0m };

        Assert.Equal(1_187.60m, BacktestRunViewBuilder.RunCharges(costModel: true, chargesPerLot: 40m, filledLots: 10, 200_000m, latest));
        Assert.Equal(0m, BacktestRunViewBuilder.RunCharges(costModel: true, chargesPerLot: 40m, filledLots: 10, 200_000m, latest: null));
    }

    [Theory]
    [InlineData("{\"costs\":{\"slippage_percent\":0.5}}", true)]
    [InlineData("{\"costs\":true}", true)]
    [InlineData("{\"costs\":\"yes\"}", true)]
    [InlineData("{\"costs\":1}", true)]
    [InlineData("{\"costs\":{}}", false)]
    [InlineData("{\"costs\":false}", false)]
    [InlineData("{\"costs\":\"false\"}", false)]
    [InlineData("{\"costs\":0}", false)]
    [InlineData("{\"charges_per_lot\":40}", false)]
    [InlineData("not json", false)]
    [InlineData(null, false)]
    public void HasCostModel_ReadsTheCostsBlockAsTheRunnerDoes(string? json, bool expected)
        => Assert.Equal(expected, BacktestRunParameters.HasCostModel(json));
}
