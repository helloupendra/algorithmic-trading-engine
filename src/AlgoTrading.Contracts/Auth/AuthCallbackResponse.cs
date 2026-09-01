using System;
using System.Collections.Generic;
using System.Text;


namespace AlgoTrading.Contracts.Auth
{
    /// <summary>
    /// Data Transfer Object representing the payload received from the broker's OAuth authentication callback.
    /// Used by the API layer to capture authorization codes or error states.
    /// </summary>
    public class AuthCallbackResponse
    {
        /// <summary>
        /// General message or description returned by the broker.
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// The temporary authorization code to be exchanged for a long-lived access token.
        /// </summary>
        public string? AuthCode { get; set; }

        /// <summary>
        /// Opaque value used to maintain state between the request and the callback, protecting against CSRF.
        /// </summary>
        public string? State { get; set; }

        /// <summary>
        /// The status text (e.g., "ok", "error").
        /// </summary>
        public string? Status { get; set; }

        /// <summary>
        /// The numeric response code returned by the broker.
        /// </summary>
        public int? Code { get; set; }
    }
}
