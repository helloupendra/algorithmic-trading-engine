using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Worker.MarketData.Config
{
    public class MarketDataWorkerSettings
    {
        public List<string> Symbols { get; set; } = new();
        public string Resolution { get; set; } = "D";
        public int LookbackDays { get; set; } = 5;
        public int IntervalMinutes { get; set; } = 15;
    }
}
