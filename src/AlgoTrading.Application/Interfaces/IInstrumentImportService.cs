using AlgoTrading.Contracts.Instruments;
using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Application.Interfaces
{
    /// <summary>
    /// Service responsible for bulk importing instrument master data into the local database.
    /// </summary>
    public interface IInstrumentImportService
    {
        /// <summary>
        /// Reads a broker-provided CSV file from the local disk and upserts the records.
        /// </summary>
        Task<ImportInstrumentsResponse> ImportFromLocalCsvAsync(
            string filepath,
            CancellationToken cancellationToken = default);
    }
}
