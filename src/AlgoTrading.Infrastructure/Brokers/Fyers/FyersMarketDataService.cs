using System.Net.Http.Headers;
using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Config;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Brokers.Fyers;

/// <summary>
/// Implementation of <see cref="IMarketDataService"/> that communicates directly with the Fyers Historical Data API.
/// Fetches historical OHLCV data and synchronizes it into the local database.
/// </summary>
public class FyersMarketDataService : IMarketDataService
{
        private readonly IBrokerCredentialsProvider _credentials;
    private readonly FyersSettings _settings;
    private readonly IBrokerSessionStore _brokerSessionStore;
    private readonly TradingDbContext _dbContext;
    private readonly ILogger<FyersMarketDataService> _logger;

    public FyersMarketDataService(
        IOptions<FyersSettings> settings,
        IBrokerSessionStore brokerSessionStore,
        TradingDbContext dbContext,
        ILogger<FyersMarketDataService> logger,
            IBrokerCredentialsProvider credentials)
    {
            _credentials = credentials;
        _settings = settings.Value;
        _brokerSessionStore = brokerSessionStore;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<IReadOnlyList<CandleResponse>> SyncHistoryAsync(
        SyncHistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var session = await _brokerSessionStore.GetCurrentAsync(cancellationToken);


        if (session is null || !session.IsAuthenticated || string.IsNullOrWhiteSpace(session.AccessToken))
        {
            throw new InvalidOperationException("No valid FYERS session found. Please authenticate first.");
        }


        string normalizedResolution = NormalizeResolution(request.Resolution);

        string rangeFrom = request.DateFormat == 1
            ? request.FromDate.ToString("yyyy-MM-dd")
            : new DateTimeOffset(
                request.FromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
                .ToUnixTimeSeconds()
                .ToString();

        string rangeTo = request.DateFormat == 1
            ? request.ToDate.ToString("yyyy-MM-dd")
            : new DateTimeOffset(
                request.ToDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))
                .ToUnixTimeSeconds()
                .ToString();

        var queryParams = new Dictionary<string, string?>
        {
            ["symbol"] = request.Symbol,
            ["resolution"] = normalizedResolution,
            ["date_format"] = request.DateFormat.ToString(),
            ["range_from"] = rangeFrom,
            ["range_to"] = rangeTo,
            ["cont_flag"] = request.ContFlag.ToString()
        };

        string baseUrl = $"{_settings.DataApiBaseUrl.TrimEnd('/')}/data/history";
        string url = QueryHelpers.AddQueryString(baseUrl, queryParams);

        Console.WriteLine("========== FYERS HISTORY DEBUG ==========");
        Console.WriteLine($"rangeFrom: {rangeFrom}");
        Console.WriteLine($"rangeTo  : {rangeTo}");
        Console.WriteLine($"URL      : {url}");
        Console.WriteLine("=========================================");

        using var httpClient = new HttpClient();

        var creds = await _credentials.GetFyersAsync(cancellationToken);
            string authHeaderValue = $"{creds.ClientId}:{session.AccessToken}";
        httpClient.DefaultRequestHeaders.Remove("Authorization");
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authHeaderValue);

        httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.GetAsync(url, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);

        Console.WriteLine("========== FYERS HISTORY RESPONSE ==========");
        Console.WriteLine(json);
        Console.WriteLine("============================================");

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"FYERS history API failed. HTTP {(int)response.StatusCode}: {json}");
        }

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        string status = root.TryGetProperty("s", out var sProp)
            ? sProp.GetString() ?? string.Empty
            : string.Empty;

        int code = root.TryGetProperty("code", out var codeProp)
            ? codeProp.GetInt32()
            : 0;

        if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) || code != 200)
        {
            throw new InvalidOperationException($"FYERS history API returned an error response: {json}");
        }

        if (!root.TryGetProperty("candles", out var candlesProp) ||
            candlesProp.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<CandleResponse>();
        }

        var result = new List<CandleResponse>();
        var entitiesToInsert = new List<Candle>();

        foreach (var candle in candlesProp.EnumerateArray())
        {
            if (candle.ValueKind != JsonValueKind.Array || candle.GetArrayLength() < 6)
                continue;

            long epochSeconds = candle[0].GetInt64();
            decimal open = Convert.ToDecimal(candle[1].GetDouble());
            decimal high = Convert.ToDecimal(candle[2].GetDouble());
            decimal low = Convert.ToDecimal(candle[3].GetDouble());
            decimal close = Convert.ToDecimal(candle[4].GetDouble());
            long volume = candle[5].GetInt64();

            DateTime timestampUtc = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;

            result.Add(new CandleResponse
            {
                Symbol = request.Symbol,
                Resolution = normalizedResolution,
                TimestampUtc = timestampUtc,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume
            });

            entitiesToInsert.Add(new Candle
            {
                Symbol = request.Symbol,
                Resolution = normalizedResolution,
                TimeStampUtc = timestampUtc,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume
            });
        }

        //if (entitiesToInsert.Count > 0)
        //{
        //    var timestamps = entitiesToInsert.Select(x => x.TimeStampUtc).ToList();

        //    var existingTimestamps = await _dbContext.Candles
        //        .Where(x =>
        //            x.Symbol == request.Symbol &&
        //            x.Resolution == normalizedResolution &&
        //            timestamps.Contains(x.TimeStampUtc))
        //        .Select(x => x.TimeStampUtc)
        //        .ToListAsync(cancellationToken);

        //    var newEntities = entitiesToInsert
        //        .Where(x => !existingTimestamps.Contains(x.TimeStampUtc))
        //        .ToList();

        //    if (newEntities.Count > 0)
        //    {
        //        await _dbContext.Candles.AddRangeAsync(newEntities, cancellationToken);
        //        await _dbContext.SaveChangesAsync(cancellationToken);
        //    }
        //}

        int insertedCount = 0;
        int updatedCount = 0;
        int skippedCount = 0;

        // FYERS occasionally repeats a bar (the in-progress candle is sent twice
        // around a range boundary). Two rows with the same timestamp in one
        // AddRange would trip the (Symbol, Resolution, TimeStampUtc) unique
        // index and roll the whole batch back, so keep the last copy of each.
        entitiesToInsert = entitiesToInsert
            .GroupBy(x => x.TimeStampUtc)
            .Select(g => g.Last())
            .OrderBy(x => x.TimeStampUtc)
            .ToList();

        if(entitiesToInsert.Count > 0)
        {
            var timestamps = entitiesToInsert.Select(x => x.TimeStampUtc).ToList();

            var existingCandles = await _dbContext.Candles
                .Where(x =>
                    x.Symbol == request.Symbol &&
                    x.Resolution == normalizedResolution &&
                    timestamps.Contains(x.TimeStampUtc))
                .ToListAsync(cancellationToken);

            var existingMap = existingCandles.ToDictionary(x => x.TimeStampUtc);

            var newEntities = new List<Candle>();

            foreach (var incoming in entitiesToInsert)
            {
                if (!existingMap.TryGetValue(incoming.TimeStampUtc, out var existing))
                {
                    newEntities.Add(incoming);
                    insertedCount++;
                }
                else
                {
                    bool changed =
                        existing.Open != incoming.Open ||
                        existing.High != incoming.High ||
                        existing.Low != incoming.Low ||
                        existing.Close != incoming.Close ||
                        existing.Volume != incoming.Volume;

                    if (changed)
                    {
                        existing.Open = incoming.Open;
                        existing.High = incoming.High;
                        existing.Low = incoming.Low;
                        existing.Close = incoming.Close;
                        existing.Volume = incoming.Volume;

                        updatedCount++;
                    }
                    else
                    {
                        skippedCount++;
                    }
                }

            }

            if (newEntities.Count > 0)
            {
                await _dbContext.Candles.AddRangeAsync(newEntities, cancellationToken);
            }

            if (newEntities.Count > 0 || updatedCount > 0)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

        }

        _logger.LogInformation(
            "History sync summary for {Symbol} ({Resolution}) -> Fetched: {Fetched}, Inserted: {Inserted}, Updated: {Updated}, Skipped: {Skipped}",
            request.Symbol,
           normalizedResolution,
           result.Count,
           insertedCount,
           updatedCount,
           skippedCount);

        return result;
    }

    /// <summary>
    /// Canonical candle code via <see cref="ResolutionCodes"/>: "5m"/"5"/"5M" → "5",
    /// "1m" → "1", "1D" → "D". FYERS history accepts exactly these codes, and it is
    /// what the candles table stores.
    /// </summary>
    private static string NormalizeResolution(string resolution)
        => ResolutionCodes.ToCandle(resolution);

    public async Task<IReadOnlyList<CandleResponse>> GetStoredHistoryAsync(
        GetStoredCandlesRequest request,
        CancellationToken cancellationToken = default)
    { 
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Symbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(request.Symbol));
        }

        string normalizedResolution = NormalizeResolution(request.Resolution);

        var query = _dbContext.Candles
            .AsNoTracking()
            .Where(x =>
                x.Symbol == request.Symbol &&
                x.Resolution == normalizedResolution);

        if (request.FromDate.HasValue)
        {
            DateTime fromUtc = request.FromDate.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            query = query.Where(x => x.TimeStampUtc >= fromUtc);
        }

        if (request.ToDate.HasValue)
        {
            DateTime toUtcExclusive = request.ToDate.Value
                .AddDays(1)
                .ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

            query = query.Where(x => x.TimeStampUtc < toUtcExclusive);
        }

        var candles = await query
            .OrderBy(x => x.TimeStampUtc)
            .Select(x => new CandleResponse
            {
                Symbol = x.Symbol,
                Resolution = x.Resolution,
                TimestampUtc = x.TimeStampUtc,
                Open = x.Open,
                High = x.High,
                Low = x.Low,
                Close = x.Close,
                Volume = x.Volume
            })
            .ToListAsync(cancellationToken);

        return candles;
    }
}