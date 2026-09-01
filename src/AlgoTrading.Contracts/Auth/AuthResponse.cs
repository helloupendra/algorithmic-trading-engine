using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Contracts.Auth
{
    public class AuthResponse
    {
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public int ExpiresInSeconds { get; set; }

        public AuthUserResponse User { get; set; } = new();
    }

    public class AuthUserResponse
    {
        public long Id { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;

        /// <summary>
        /// "Admin" or "Trader". Drives which navigation and routes the web client renders.
        /// This is a convenience mirror of the token's role claim — the API authorizes on
        /// the token, never on this value.
        /// </summary>
        public string Role { get; set; } = string.Empty;
    }
}
