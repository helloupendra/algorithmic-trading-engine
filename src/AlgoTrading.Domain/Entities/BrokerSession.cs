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
        /// Operator-facing broker name (e.g. "FYERS"). Display only — routing and
        /// lookups go through <see cref="ProviderKey"/>.
        /// </summary>
        public string BrokerName { get; set; } = string.Empty;

        /// <summary>
        /// The connector this session belongs to, e.g. "fyers". Each broker keeps
        /// its own session: connecting a second broker must never silently
        /// invalidate the first one's token.
        /// </summary>
        public string ProviderKey { get; set; } = string.Empty;

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
        /// True when an access token exists, the session is active, and the
        /// token has not passed its broker-imposed expiry.
        /// </summary>
        /// <remarks>
        /// The expiry matters as much as the token itself. FYERS invalidates
        /// every access token at 06:00 IST regardless of when it was issued, and
        /// on 2026-09-10 the platform said "authenticated" for a token issued
        /// the previous afternoon: the ingestor connected with it, FYERS
        /// answered "Token is expired", and every strategy sat deaf until a
        /// person signed in. Reporting the expiry here is what turns that into
        /// an honest "sign in" prompt.
        /// </remarks>
        public bool IsAuthenticated => IsAuthenticatedAt(DateTime.UtcNow);

        public bool IsAuthenticatedAt(DateTime utcNow)
            => !string.IsNullOrWhiteSpace(AccessToken) && IsActive && !IsExpiredAt(utcNow);

        /// <summary>
        /// When this session's access token stops working, or null when the
        /// broker's rule is not known and the token has to be trusted until it
        /// is refused.
        /// </summary>
        public DateTime? ExpiresAtUtc => TokenExpiryUtc(ProviderKey, BrokerName, UpdatedUtc);

        public bool IsExpiredAt(DateTime utcNow)
            => ExpiresAtUtc is { } expiry && utcNow >= expiry;

        /// <summary>
        /// FYERS access tokens are valid until 06:00 IST following their issue,
        /// however late in the day they were issued. Other brokers: unknown.
        /// </summary>
        public static DateTime? TokenExpiryUtc(string? providerKey, string? brokerName, DateTime issuedUtc)
        {
            var isFyers = string.Equals(providerKey, "fyers", StringComparison.OrdinalIgnoreCase)
                          || (string.IsNullOrWhiteSpace(providerKey)
                              && string.Equals(brokerName, "FYERS", StringComparison.OrdinalIgnoreCase));
            if (!isFyers) return null;

            // India Standard Time is a fixed +05:30 with no daylight saving, so
            // the arithmetic needs no zone database (the Domain has none).
            var ist = DateTime.SpecifyKind(issuedUtc, DateTimeKind.Utc) + IstOffset;
            var sixAm = ist.Date.AddHours(6);
            if (ist >= sixAm) sixAm = sixAm.AddDays(1);
            return DateTime.SpecifyKind(sixAm - IstOffset, DateTimeKind.Utc);
        }

        private static readonly TimeSpan IstOffset = TimeSpan.FromMinutes(330);
    }
}
