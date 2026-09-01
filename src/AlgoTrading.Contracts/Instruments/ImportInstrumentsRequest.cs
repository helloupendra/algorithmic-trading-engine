using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.Instruments
{
    /// <summary>
    /// Data Transfer Object representing a request to trigger a local CSV import of instrument master data.
    /// </summary>
    public class ImportInstrumentsRequest
    {
        /// <summary>
        /// The absolute local file path to the broker-provided CSV file containing instrument definitions.
        /// </summary>
        public string FilePath { get; set; } = string.Empty;
    }
}
