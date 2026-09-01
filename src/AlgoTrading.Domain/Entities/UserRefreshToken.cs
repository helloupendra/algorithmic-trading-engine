using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Domain.Entities
{
    public class UserRefreshToken
    {
        public long Id { get; set; }

        public long UserId { get; set; }
        public AppUser? User { get; set; }

        public string TokenHash { get; set; } = string.Empty;

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime ExpiresUtc { get; set; }
        
        public DateTime? RevokedUtc { get; set; }
        public string? ReplacedByTokenHash { get; set; }

        public bool IsRevoked => RevokedUtc.HasValue;
        public bool IsExpired => DateTime.UtcNow >= ExpiresUtc;
        public bool IsActive => !IsRevoked && !IsExpired;
    }
}
