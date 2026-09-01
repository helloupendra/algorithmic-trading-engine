using AlgoTrading.Domain.Enums;
using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Domain.Entities
{
    public class ExpiryRule
    {
        public long Id { get; set; }

        public string Exchange { get; set; } = string.Empty; //NSE / BSE
        public string Underlying { get; set; } = string.Empty; //Banknifty / Sensex

        public bool HasWeekly { get; set; }
        public bool HasMonthly { get; set; }
        public bool HasQuarterly { get; set; }
        public bool HasSemiAnnual { get; set; }

        public ExpiryDayOfWeek? WeeklyExpiryDay { get; set; }
        public ExpiryDayOfWeek? MonthlyExpiryDay { get; set; }
        public ExpiryDayOfWeek? QuarterlyExpiryDay { get; set; }
        public ExpiryDayOfWeek? SemiAnnualExpiryDay { get; set; }

        public HolidayShiftRule HolidayShiftRule { get; set; } = HolidayShiftRule.PreviousTradingDay;

        //Strategy preference 
        public ExpiryType PreferredExpiryType { get; set; } = ExpiryType.Monthly;
        public bool IsEnabled { get; set; } = true;

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }
}
