using System;

namespace AlgoTrading.Infrastructure.SeedData
{
    public class UserSeedItem
    {
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public decimal TotalCapital { get; set; }

        /// <summary>
        /// Optional role. Defaults to Trader when omitted — a seed file can never
        /// silently mint an administrator.
        /// </summary>
        public string? Role { get; set; }
    }

    public class StrategySeedItem
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string DefaultParametersJson { get; set; } = "{}";
    }

    public class LiveWatchlistSeedItem
    {
        public string Symbol { get; set; } = string.Empty;
        public string DataType { get; set; } = "symbolUpdate";
        public bool IsActive { get; set; } = true;
        public int Priority { get; set; } = 0;
    }
}
