// src/AlgoTrading.Contracts/Options/BackfillOptionHistoryRequest.cs
namespace AlgoTrading.Contracts.Options;

public class BackfillOptionHistoryRequest
{
    public string Exchange { get; set; } = "NSE";
    public string Underlying { get; set; } = "BANKNIFTY";

    /// <summary>
    /// Optional exact expiry date.
    /// If null, the service should resolve expiry using ExpiryResolver.
    /// </summary>
    public DateOnly? ExpiryDate { get; set; }

    /// <summary>
    /// Optional underlying price used to derive ATM strike.
    /// If null, service may fetch latest live underlying price.
    /// </summary>
    public decimal? UnderlyingPrice { get; set; }

    /// <summary>
    /// Optional exact ATM strike.
    /// If null, ATM should be derived from UnderlyingPrice using StrikeStep.
    /// </summary>
    public decimal? AtmStrike { get; set; }

    /// <summary>
    /// Example: 2 means ATM ± 2 strikes.
    /// ATM=54500, step=100 => 54300, 54400, 54500, 54600, 54700
    /// </summary>
    public int StrikeCountEachSide { get; set; } = 2;

    /// <summary>
    /// Strike gap size. BANKNIFTY is usually 100.
    /// </summary>
    public decimal StrikeStep { get; set; } = 100;

    /// <summary>
    /// Candle resolution, e.g. 1m, 5m, 15m, D
    /// </summary>
    public string Resolution { get; set; } = "1m";

    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }

    public bool IncludeCalls { get; set; } = true;
    public bool IncludePuts { get; set; } = true;
}