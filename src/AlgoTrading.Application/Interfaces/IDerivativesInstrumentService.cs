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
        Task<IReadOnlyList<DerivativeExpiryResponse>> GetExpiriesAsync(
            string underlying,
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
        /// </summary>
        Task<OptionChainItemResponse?> GetExactContractAsync(
            string underlying,
            DateOnly expiryDate,
            decimal strike,
            string optionType,
            CancellationToken cancellationToken = default);
    }

}
