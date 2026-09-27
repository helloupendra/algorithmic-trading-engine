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

        /// <summary>
        /// The module keys (<c>PlatformModules</c>) a trader has been granted,
        /// so the console shows exactly the workspaces and tabs the API will
        /// answer. Null for an admin, who holds every module by role, and on
        /// the user list, which does not read grants.
        /// </summary>
        public List<string>? ModuleGrants { get; set; }
    }
}
