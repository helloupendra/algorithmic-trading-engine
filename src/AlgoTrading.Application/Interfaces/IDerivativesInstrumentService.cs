using AlgoTrading.Contracts.Instruments;
using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Application.Interfaces
{

    /// <summary>
    /// Service to query and filter the local instrument database for derivative contracts (Options).
    /// </summary>
    public interface IDerivativesInstrumentService
    {
        /// <summary>
        /// Retrieves all available expiry dates for a given underlying asset.
        /// </summary>
        /// <summary>
        /// The nearest futures contract of this underlying that has not expired,
        /// e.g. "MCX:CRUDEOIL26SEPFUT". Null when the master holds none.
        /// </summary>
        /// <remarks>
        /// This is what stands in for a spot price on MCX, where no spot quote
        /// exists. It is resolved rather than configured because the answer
        /// changes every month.
        /// </remarks>
        Task<string?> GetNearestFutureSymbolAsync(
            string underlying,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Option expiry dates of an underlying. With <paramref name="includeHistory"/>, an
        /// index also gets every expired expiry the exchange calendar and the master know
        /// (backtests); without it, only listed contracts count.
        /// </summary>
        Task<IReadOnlyList<DerivativeExpiryResponse>> GetExpiriesAsync(
            string underlying,
            bool includeHistory = false,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves a filtered option chain (CE and PE) for a specific underlying and expiry date.
        /// Optionally filters by a strike price range.
        /// </summary>
        Task<IReadOnlyList<OptionChainItemResponse>> GetOptionChainAsync(
            string underlying,
            DateOnly expiryDate,
            decimal? fromStrike = null,
            decimal? toStrike = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resolves an exact option contract based on underlying, expiry, strike, and type (CE/PE).
        /// With <paramref name="includeHistory"/>, an expired contract is found too: from the
        /// master when it still holds the row, else from the stored option history (backtests).
        /// </summary>
        Task<OptionChainItemResponse?> GetExactContractAsync(
            string underlying,
            DateOnly expiryDate,
            decimal strike,
            string optionType,
            bool includeHistory = false,
            CancellationToken cancellationToken = default);
    }

}
