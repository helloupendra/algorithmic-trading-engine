using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Domain.Entities
{
    public class AppUser
    {
        public long Id { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;

        public string PasswordHash { get; set; } = string.Empty;

        /// <summary>
        /// Authorization role, one of <see cref="Constants.UserRoles"/>.
        /// New accounts default to Trader; Admin must be granted deliberately.
        /// </summary>
        public string Role { get; set; } = Constants.UserRoles.Trader;

        public decimal TotalCapital { get; set; } = 0m;

        public bool IsActive { get; set; } = true;

        /// <summary>
        /// How many live runs this trader may hold open at once. Null means the
        /// platform-wide limit applies; a number overrides it for this account
        /// alone, which is how one trader is given more rope than another.
        /// </summary>
        public int? MaxConcurrentRuns { get; set; }

        /// <summary>The modules this account may use. Empty means it can do nothing.</summary>
        public ICollection<UserModuleGrant> ModuleGrants { get; set; } = new List<UserModuleGrant>();

        /// <summary>
        /// The strategy package this trader holds. Null means none, which means no
        /// strategies — deny by default, same as module grants.
        /// </summary>
        public long? StrategyPackageId { get; set; }
        public StrategyPackage? StrategyPackage { get; set; }

        /// <summary>Extra strategies granted to this trader beyond their package.</summary>
        public ICollection<UserStrategyGrant> StrategyGrants { get; set; } = new List<UserStrategyGrant>();

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        public DateTime? LastLoginUtc { get; set; }

        public ICollection<UserRefreshToken> RefreshTokens { get; set; } = new List<UserRefreshToken>();
    }
}
