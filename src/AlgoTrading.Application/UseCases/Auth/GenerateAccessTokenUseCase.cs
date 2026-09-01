using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Application.Interfaces;
using Newtonsoft.Json.Linq;

namespace AlgoTrading.Application.UseCases.Auth
{
    /// <summary>
    /// Use case for exchanging an authorization code for a broker access token.
    /// </summary>
    public class GenerateAccessTokenUseCase
    {
        private readonly IBrokerAuthService _brokerAuthService;

        /// <summary>
        /// Initializes a new instance of <see cref="GenerateAccessTokenUseCase"/>.
        /// </summary>
        public GenerateAccessTokenUseCase(IBrokerAuthService brokerAuthService)
        {
            _brokerAuthService = brokerAuthService;
        }

        /// <summary>
        /// Executes the token generation workflow using the provided auth code.
        /// </summary>
        public Task<JObject> ExecuteAsync(string authcode, CancellationToken cancellationToken = default)
        {
            return _brokerAuthService.GenerateAccessTokenAsync(authcode, cancellationToken);
        }
    }
}
