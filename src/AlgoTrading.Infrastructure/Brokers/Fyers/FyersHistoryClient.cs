using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Config;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AlgoTrading.Infrastructure.Session;

namespace AlgoTrading.Infrastructure.Brokers.Fyers
{
    public class FyersHistoryClient : IFyersHistoryClient
    {
        private readonly IBrokerCredentialsProvider _credentials;
        private readonly FyersSettings _settings;
        private readonly IBrokerSessionStore _brokerSessionStore;
        private readonly ILogger<FyersHistoryClient> _logger;

        public FyersHistoryClient(
            IOptions<FyersSettings> settings,
            IBrokerSessionStore brokerSessionStore,
            ILogger<FyersHistoryClient> logger,
            IBrokerCredentialsProvider credentials)
        {
            _credentials = credentials;
            _settings = settings.Value;
            _brokerSessionStore = brokerSessionStore;
            _logger = logger;
        }

        public async Task<IReadOnlyList<HistoryCandleBar>> GetHistoryAsync(
            string symbol,
            string resolution,
            DateTime fromUtc,
            DateTime toUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var session = await _brokerSessionStore.GetCurrentAsync(cancellationToken);

            if (session is null || !session.IsAuthenticated || string.IsNullOrWhiteSpace(session.AccessToken))
            {
                throw new InvalidOperationException("No valid FYERS session found. Please authenticate first.");
            }

            string rangeFrom = new DateTimeOffset(fromUtc).ToUnixTimeSeconds().ToString();
            string rangeTo = new DateTimeOffset(toUtc).ToUnixTimeSeconds().ToString();

            var queryParams = new Dictionary<string, string?>
            {
                ["symbol"] = symbol,
                ["resolution"] = resolution,
                ["date_format"] = "0", // 0 for epoch
                ["range_from"] = rangeFrom,
                ["range_to"] = rangeTo,
                ["cont_flag"] = "1"
            };

            string baseUrl = $"{_settings.DataApiBaseUrl.TrimEnd('/')}/data/history";
            string url = QueryHelpers.AddQueryString(baseUrl, queryParams);

            using var httpClient = new HttpClient();
            var creds = await _credentials.GetFyersAsync(cancellationToken);
            string authHeaderValue = $"{creds.ClientId}:{session.AccessToken}";
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authHeaderValue);
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await httpClient.GetAsync(url, cancellationToken);
            string json = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("FYERS history API failed for {Symbol}. HTTP {StatusCode}: {Json}", symbol, response.StatusCode, json);
                return Array.Empty<HistoryCandleBar>();
            }

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string status = root.TryGetProperty("s", out var sProp) ? sProp.GetString() ?? string.Empty : string.Empty;
            int code = root.TryGetProperty("code", out var codeProp) ? codeProp.GetInt32() : 0;

            if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) || code != 200)
            {
                _logger.LogWarning("FYERS history API error for {Symbol}: {Json}", symbol, json);
                return Array.Empty<HistoryCandleBar>();
            }

            if (!root.TryGetProperty("candles", out var candlesProp) || candlesProp.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<HistoryCandleBar>();
            }

            var result = new List<HistoryCandleBar>();

            foreach (var candle in candlesProp.EnumerateArray())
            {
                if (candle.ValueKind != JsonValueKind.Array || candle.GetArrayLength() < 6)
                    continue;

                long epochSeconds = candle[0].GetInt64();
                DateTime timestampUtc = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;

                result.Add(new HistoryCandleBar
                {
                    TimestampUtc = timestampUtc,
                    Open = Convert.ToDecimal(candle[1].GetDouble()),
                    High = Convert.ToDecimal(candle[2].GetDouble()),
                    Low = Convert.ToDecimal(candle[3].GetDouble()),
                    Close = Convert.ToDecimal(candle[4].GetDouble()),
                    Volume = Convert.ToDecimal(candle[5].GetDouble())
                });
            }

            return result;
        }
    }
}
