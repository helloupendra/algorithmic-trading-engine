// src/AlgoTrading.Application/Interfaces/IHistoricalCandleStore.cs
using AlgoTrading.Application.Providers;

namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// The one place bars become rows. Every data provider hands its bars here, so a
/// new vendor never re-implements dedupe, upsert or resolution normalisation.
/// </summary>
public interface IHistoricalCandleStore
{
    /// <param name="sourceKey">
    /// The connector that produced these bars; stored on every row for lineage.
    /// </param>
    Task<CandleUpsertResult> UpsertAsync(
        string symbol,
        string resolution,
        IReadOnlyList<ProviderHistoryBar> candles,
        string sourceKey,
        CancellationToken cancellationToken = default);
}

public class CandleUpsertResult
{
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
}
