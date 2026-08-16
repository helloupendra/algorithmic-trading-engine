using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Application.Interfaces
{
    public interface IFyersHistoryClient
    {
        Task<IReadOnlyList<HistoryCandleBar>> GetHistoryAsync(
            string symbol,
            string resolution,
            DateTime fromUtc,
            DateTime toUtc,
            CancellationToken cancellationToken = default);
    }

    public class HistoryCandleBar
    { 
        public DateTime TimestampUtc { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }

        public decimal Volume { get; set; }
    }
}
