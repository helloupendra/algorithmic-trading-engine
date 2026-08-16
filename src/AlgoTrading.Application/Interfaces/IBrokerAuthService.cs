using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AlgoTrading.Application.Interfaces
{
    /// <summary>
    /// Service responsible for initiating and completing the OAuth flow with the broker.
    /// </summary>
    public interface IBrokerAuthService
    {
        /// <summary>
        /// Initiates the authentication flow (e.g., launching a browser or returning an auth URL).
        /// </summary>
        Task StartAuthFlowAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Exchanges an authorization code for a long-lived access token.
        /// </summary>
        Task<JObject> GenerateAccessTokenAsync(string authCode, CancellationToken cancellationToken = default);
    }
}
