using System;

namespace AlgoTrading.Domain.Entities
{
    /// <summary>
    /// Represents a registered strategy in the system.
    /// </summary>
    public class StrategyDefinition
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string DefaultParametersJson { get; set; } = "{}";
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }
}
