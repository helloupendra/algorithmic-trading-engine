using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.Instruments
{
    /// <summary>
    /// Data Transfer Object representing the result of a local CSV instrument import operation.
    /// Used to inform the client how many records were successfully processed.
    /// </summary>
    public class ImportInstrumentsResponse
    {
        /// <summary>
        /// Total lines read from the CSV file.
        /// </summary>
        public int TotalRowsRead { get; set; }

        /// <summary>
        /// Total new records inserted into the database.
        /// </summary>
        public int Inserted { get; set; }

        /// <summary>
        /// Total existing records that were updated.
        /// </summary>
        public int Updated { get; set; }

        /// <summary>
        /// Total rows skipped due to invalid data or missing fields.
        /// </summary>
        public int Skipped { get; set; }

        /// <summary>
        /// General status or error message.
        /// </summary>
        public string Message { get; set; } = string.Empty;
    }
}
