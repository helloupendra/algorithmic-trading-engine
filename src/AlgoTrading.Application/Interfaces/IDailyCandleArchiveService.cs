using System;
using System.Threading;
using System.Threading.Tasks;
using AlgoTrading.Contracts.MarketData;

namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// Turns one IST trading day of live data into permanent candles.
/// </summary>
public interface IDailyCandleArchiveService
{
    /// <param name="istDay">The trading day, in IST.</param>
    /// <param name="includeBrokerBackfill">
    /// Also ask the broker for its own candles for the index symbols. Needs
    /// an authenticated broker session; a failure is reported, not thrown.
    /// </param>
    Task<CandleArchiveResult> ArchiveDayAsync(DateOnly istDay, bool includeBrokerBackfill, CancellationToken cancellationToken = default);
}
