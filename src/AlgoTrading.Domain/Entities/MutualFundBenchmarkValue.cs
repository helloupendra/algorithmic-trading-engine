namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One NSE total-return index's closing value on one day, from niftyindices.com: the benchmarks the fund report
/// compares schemes with. Table <c>mf_benchmark_tri</c>, a TimescaleDB hypertable on <see cref="Date"/>.
/// </summary>
/// <remarks>
/// A total-return index (TRI) assumes every dividend is reinvested, as a fund's NAV does; the price index alone would
/// flatter every fund by the dividend yield. Values are stored exactly as niftyindices.com wrote them.
/// </remarks>
public class MutualFundBenchmarkValue
{
    /// <summary>niftyindices.com's index name, upper case: "NIFTY 50", "NIFTY500 MULTICAP 50:25:25".</summary>
    public string IndexName { get; set; } = string.Empty;

    /// <summary>The trading day (IST).</summary>
    public DateOnly Date { get; set; }

    /// <summary>The total-return index's close.</summary>
    public decimal Value { get; set; }
}
