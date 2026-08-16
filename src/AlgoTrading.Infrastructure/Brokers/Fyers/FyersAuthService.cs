using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Config;
using FyersCSharpSDK;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace AlgoTrading.Infrastructure.Brokers.Fyers
{
    /// <summary>
    /// Implementation of <see cref="IBrokerAuthService"/> for the Fyers API.
    /// Handles the OAuth2 flow to generate access tokens using the Fyers C# SDK.
    /// </summary>
    public class FyersAuthService : IBrokerAuthService
    {
        private readonly FyersSettings _settings;

        public FyersAuthService(IOptions<FyersSettings> settings)
        { 
            _settings = settings.Value;
        }

        public Task StartAuthFlowAsync(CancellationToken cancellationToken = default)
        { 
            cancellationToken.ThrowIfCancellationRequested();

            FyersClass fyers = FyersClass.Instance;

            fyers.GetGenerateCode(
                _settings.ClientId,
                _settings.SecretKey,
                _settings.RedirectUri
                );

            return Task.CompletedTask;
        }

        public async Task<JObject> GenerateAccessTokenAsync(string authCode, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FyersClass fyers = FyersClass.Instance;

            string appHashId = Utility.GenerateAppHashID(_settings.ClientId, _settings.SecretKey);

            JObject tokenResponse = await fyers.GenerateToken(
                _settings.SecretKey,
                _settings.RedirectUri,
                authCode,
                appHashId
            );

            return tokenResponse;
        }
    }
}
