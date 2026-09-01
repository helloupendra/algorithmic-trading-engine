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

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        public DateTime? LastLoginUtc { get; set; }

        public ICollection<UserRefreshToken> RefreshTokens { get; set; } = new List<UserRefreshToken>();
    }
}
