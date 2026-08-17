using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.Auth
{
    public class MeResponse
    {
        public long Id { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// "Admin" or "Trader".
        /// </summary>
        public string Role { get; set; } = string.Empty;

        /// <summary>
        /// Capital allocated to this account, used by the trader panel's P&amp;L views.
        /// </summary>
        public decimal TotalCapital { get; set; }

        /// <summary>
        /// Deactivated accounts cannot log in. Managed from the admin panel.
        /// </summary>
        public bool IsActive { get; set; }

        public DateTime CreatedUtc { get; set; }
        public DateTime? LastLoginUtc { get; set; }
    }
}
