using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Domain.Entities
{
    /// <summary>
    /// Represents an authenticated session with a broker API (e.g., Fyers).
    /// Stores the tokens needed to authorize subsequent HTTP requests for live data and order execution.
    /// </summary>
    public class BrokerSession
    {
        /// <summary>
        /// Primary key.
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// The name of the broker (e.g., "FYERS"). Used if the system scales to support multiple brokers.
        /// </summary>
        public string BrokerName { get; set; } = string.Empty;

        /// <summary>
        /// The <see cref="BrokerAccount"/> this session belongs to. Null means the
        /// shared platform account, which is how the installation runs today.
        /// </summary>
        public long? BrokerAccountId { get; set; }

        /// <summary>
        /// The short-lived access token used in Authorization headers.
        /// </summary>
        public string AccessToken { get; set; } = string.Empty;

        /// <summary>
        /// The long-lived refresh token used to obtain a new access token when the current one expires.
        /// </summary>
        public string RefreshToken { get; set; } = string.Empty;

        /// <summary>
        /// The timestamp when this session was initially created.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// The timestamp when this session's tokens were last refreshed or updated.
        /// </summary>
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Indicates whether this session is currently active and allowed to be used.
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// A computed property that returns true if an AccessToken exists and the session is marked as active.
        /// </summary>
        public bool IsAuthenticated => !string.IsNullOrWhiteSpace(AccessToken) && IsActive;
    }
}
