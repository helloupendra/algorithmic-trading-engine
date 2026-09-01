using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.Equities
{
    public class AddEquityGroupToWatchlistResponse
    {
        public string GroupName { get; set; } = string.Empty;
        public int TotalMemberResolved { get; set; }
        public int Upserted { get; set; }
        public int Skipped { get; set; }

        public List<string> Symbols { get; set; } = new();

        public string Message { get; set; } = string.Empty;
    }
}
