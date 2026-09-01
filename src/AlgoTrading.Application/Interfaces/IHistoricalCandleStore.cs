// src/AlgoTrading.Application/Interfaces/IHistoricalCandleStore.cs
namespace AlgoTrading.Application.Interfaces;

public interface IHistoricalCandleStore
{
    Task<CandleUpsertResult> UpsertAsync(
        string symbol,
        string resolution,
        IReadOnlyList<HistoryCandleBar> candles,
        CancellationToken cancellationToken = default);
}

public class CandleUpsertResult
{
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
}