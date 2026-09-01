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
        private readonly IBrokerCredentialsProvider _credentials;

        public FyersAuthService(
            IOptions<FyersSettings> settings,
            IBrokerCredentialsProvider credentials)
        { 
            _settings = settings.Value;
            _credentials = credentials;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Built by hand (FYERS API v3 generate-authcode) instead of via the SDK:
        /// the SDK's GetGenerateCode opens a browser on the *server*, which is
        /// useless when the operator is on the web console. The frontend sends
        /// the user's own browser to this URL; FYERS then redirects to our
        /// configured callback with an auth_code.
        /// </remarks>
        public async Task<string> GetAuthUrlAsync(string? state = null, CancellationToken cancellationToken = default)
        {
            var creds = await _credentials.GetFyersAsync(cancellationToken);
            if (creds.Source == "none")
            {
                throw new InvalidOperationException(
                    "FYERS app credentials are not configured. Save them on the Broker page first.");
            }

            return "https://api-t1.fyers.in/api/v3/generate-authcode" +
                   $"?client_id={Uri.EscapeDataString(creds.ClientId)}" +
                   $"&redirect_uri={Uri.EscapeDataString(creds.RedirectUri)}" +
                   "&response_type=code" +
                   $"&state={Uri.EscapeDataString(state ?? "webui")}";
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

            var creds = await _credentials.GetFyersAsync(cancellationToken);

            FyersClass fyers = FyersClass.Instance;

            string appHashId = Utility.GenerateAppHashID(creds.ClientId, creds.SecretKey);

            JObject tokenResponse = await fyers.GenerateToken(
                creds.SecretKey,
                creds.RedirectUri,
                authCode,
                appHashId
            );

            return tokenResponse;
        }
    }
}
