using System.Net;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace AlgoTrading.Infrastructure.Providers.Dhan;

/// <summary>What one window of one series did.</summary>
/// <param name="Empty">Dhan answered no bars although the window has trading days.</param>
public sealed record DhanOptionWindowResult(int Fetched, int Inserted, int Retries, bool Empty);

/// <summary>One series' stored days, for the coverage endpoint.</summary>
public sealed class OptionHistoryCoverageRow
{
    public string ExpiryFlag { get; set; } = string.Empty;
    public int ExpiryCode { get; set; }
    public string Resolution { get; set; } = string.Empty;
    public int StrikeOffset { get; set; }
    public string OptionType { get; set; } = string.Empty;
    public DateOnly Day { get; set; }
    public long Bars { get; set; }
}

/// <summary>
/// Fetches windows of Dhan's expired options series and stores them in
/// <c>option_history_bars</c>. Scoped: one database context and one API client
/// per unit of work, so the job runner can run several side by side while
/// <see cref="DhanRateGate"/> keeps the account inside its request budget.
/// </summary>
public sealed class DhanOptionHistoryImporter
{
    /// <summary>Waits before each retry of a transient failure.</summary>
    internal static readonly TimeSpan[] Backoff =
    {
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
    };

    /// <summary>Rows per INSERT statement.</summary>
    private const int InsertBatch = 5000;

    private readonly DhanApiClient _api;
    private readonly TradingDbContext _db;
    private readonly ILogger<DhanOptionHistoryImporter> _logger;

    public DhanOptionHistoryImporter(DhanApiClient api, TradingDbContext db, ILogger<DhanOptionHistoryImporter> logger)
    {
        _api = api;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// The days the underlying's index traded in [from, to], from Dhan's daily
    /// index bars: the exchange's own calendar, holidays and special sessions
    /// included, for years the platform's holiday table does not cover.
    /// </summary>
    public async Task<IReadOnlyList<DateOnly>> TradingDaysAsync(string underlying, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        _ = DhanRollingOptions.OptionUnderlying(underlying);
        var index = DhanInstruments.IndexUnderlyings[underlying.Trim().ToUpperInvariant()];
        var fromUtc = IstTime.StartOfDayUtc(from);
        var toUtc = IstTime.StartOfDayUtc(to);

        var days = new SortedSet<DateOnly>();
        foreach (var (windowFrom, windowTo) in DhanHistory.Windows(fromUtc, toUtc.AddDays(1), intraday: false))
        {
            using var document = await _api.PostAsync(
                "/charts/historical", DhanHistory.DailyRequest(index, windowFrom, windowTo), DhanRateClass.Data, cancellationToken);
            foreach (var bar in DhanHistory.Parse(document.RootElement, windowFrom, windowTo, intraday: false))
            {
                var day = IstTime.DateOf(bar.TimestampUtc);
                if (day >= from && day <= to) days.Add(day);
            }
        }

        return days.ToList();
    }

    /// <summary>The IST days in [from, to] on which a series already has bars.</summary>
    public async Task<HashSet<DateOnly>> PresentDaysAsync(DhanRollingSeries series, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var days = await _db.Database.SqlQueryRaw<DateOnly>(
                """
                SELECT DISTINCT ("BarStartUtc" AT TIME ZONE 'Asia/Kolkata')::date AS "Value"
                FROM option_history_bars
                WHERE "Underlying" = @underlying AND "ExpiryFlag" = @flag AND "ExpiryCode" = @code
                  AND "StrikeOffset" = @offset AND "OptionType" = @type AND "Resolution" = @resolution
                  AND "BarStartUtc" >= @fromUtc AND "BarStartUtc" < @toUtc
                """,
                new NpgsqlParameter("underlying", series.Underlying),
                new NpgsqlParameter("flag", series.ExpiryFlag),
                new NpgsqlParameter("code", series.ExpiryCode),
                new NpgsqlParameter("offset", series.StrikeOffset),
                new NpgsqlParameter("type", series.OptionType),
                new NpgsqlParameter("resolution", series.Resolution),
                new NpgsqlParameter("fromUtc", IstTime.StartOfDayUtc(from)),
                new NpgsqlParameter("toUtc", IstTime.StartOfDayUtc(to.AddDays(1))))
            .ToListAsync(cancellationToken);
        return days.ToHashSet();
    }

    /// <summary>Fetches one window of one series and stores what is not stored yet.</summary>
    /// <param name="onRetry">Called before each wait, with the failure being retried.</param>
    /// <exception cref="DhanApiException">A refusal that retrying will not fix.</exception>
    public async Task<DhanOptionWindowResult> ImportWindowAsync(
        DhanRollingSeries series,
        DateOnly from,
        DateOnly to,
        Action<Exception>? onRetry,
        CancellationToken cancellationToken)
    {
        int retries = 0;
        List<OptionHistoryBar> rows;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var document = await _api.PostAsync(
                    DhanRollingOptions.Path, DhanRollingOptions.Request(series, from, to), DhanRateClass.Data, cancellationToken);
                rows = DhanRollingOptions.Parse(document.RootElement, series, from, to);
                break;
            }
            catch (Exception ex) when (attempt < Backoff.Length && IsTransient(ex, cancellationToken))
            {
                retries++;
                onRetry?.Invoke(ex);
                _logger.LogWarning("Dhan option history {Series} {From}..{To}: {Message}; retrying in {Seconds}s.",
                    series, from, to, ex.Message, Backoff[attempt].TotalSeconds);
                await Task.Delay(Backoff[attempt], cancellationToken);
            }
        }

        int inserted = rows.Count == 0 ? 0 : await InsertAsync(rows, cancellationToken);
        return new DhanOptionWindowResult(rows.Count, inserted, retries, Empty: rows.Count == 0);
    }

    /// <summary>
    /// Inserts rows, skipping any whose series and bar are already stored. Returns
    /// the number actually inserted.
    /// </summary>
    public async Task<int> InsertAsync(IReadOnlyList<OptionHistoryBar> rows, CancellationToken cancellationToken)
    {
        int inserted = 0;
        foreach (var batch in rows.Chunk(InsertBatch))
        {
            inserted += await _db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO option_history_bars
                    ("Underlying", "ExpiryFlag", "ExpiryCode", "ExpiryDate", "StrikeOffset", "Strike", "OptionType", "Resolution",
                     "BarStartUtc", "Open", "High", "Low", "Close", "Volume", "OpenInterest", "ImpliedVolatility", "SpotPrice", "SourceKey")
                SELECT * FROM unnest(
                    @underlying, @flag, @code, @expiry, @offset, @strike, @type, @resolution,
                    @start, @open, @high, @low, @close, @volume, @oi, @iv, @spot, @source)
                ON CONFLICT DO NOTHING
                """,
                new object[]
                {
                    Array("underlying", NpgsqlDbType.Text, batch.Select(r => r.Underlying).ToArray()),
                    Array("flag", NpgsqlDbType.Text, batch.Select(r => r.ExpiryFlag).ToArray()),
                    Array("code", NpgsqlDbType.Integer, batch.Select(r => r.ExpiryCode).ToArray()),
                    Array("expiry", NpgsqlDbType.Date, batch.Select(r => r.ExpiryDate).ToArray()),
                    Array("offset", NpgsqlDbType.Integer, batch.Select(r => r.StrikeOffset).ToArray()),
                    Array("strike", NpgsqlDbType.Numeric, batch.Select(r => r.Strike).ToArray()),
                    Array("type", NpgsqlDbType.Text, batch.Select(r => r.OptionType).ToArray()),
                    Array("resolution", NpgsqlDbType.Text, batch.Select(r => r.Resolution).ToArray()),
                    Array("start", NpgsqlDbType.TimestampTz, batch.Select(r => DateTime.SpecifyKind(r.BarStartUtc, DateTimeKind.Utc)).ToArray()),
                    Array("open", NpgsqlDbType.Numeric, batch.Select(r => r.Open).ToArray()),
                    Array("high", NpgsqlDbType.Numeric, batch.Select(r => r.High).ToArray()),
                    Array("low", NpgsqlDbType.Numeric, batch.Select(r => r.Low).ToArray()),
                    Array("close", NpgsqlDbType.Numeric, batch.Select(r => r.Close).ToArray()),
                    Array("volume", NpgsqlDbType.Bigint, batch.Select(r => r.Volume).ToArray()),
                    Array("oi", NpgsqlDbType.Bigint, batch.Select(r => r.OpenInterest).ToArray()),
                    Array("iv", NpgsqlDbType.Numeric, batch.Select(r => r.ImpliedVolatility).ToArray()),
                    Array("spot", NpgsqlDbType.Numeric, batch.Select(r => r.SpotPrice).ToArray()),
                    Array("source", NpgsqlDbType.Text, batch.Select(r => r.SourceKey).ToArray()),
                },
                cancellationToken);
        }

        return inserted;
    }

    /// <summary>Bars per stored day for every series of an underlying.</summary>
    public async Task<List<OptionHistoryCoverageRow>> CoverageAsync(string underlying, CancellationToken cancellationToken) =>
        await _db.Database.SqlQueryRaw<OptionHistoryCoverageRow>(
                """
                SELECT "ExpiryFlag", "ExpiryCode", "Resolution", "StrikeOffset", "OptionType",
                       ("BarStartUtc" AT TIME ZONE 'Asia/Kolkata')::date AS "Day", count(*) AS "Bars"
                FROM option_history_bars
                WHERE "Underlying" = @underlying
                GROUP BY 1, 2, 3, 4, 5, 6
                ORDER BY 1, 2, 3, 4, 5, 6
                """,
                new NpgsqlParameter("underlying", underlying.Trim().ToUpperInvariant()))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// A failure worth waiting out: throttling, Dhan's own server errors, and the
    /// network. Never a refused token or a bad request, which fail the same way
    /// every time, and never the caller's own cancellation.
    /// </summary>
    internal static bool IsTransient(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        _ when cancellationToken.IsCancellationRequested => false,
        DhanApiException d when d.IsAuthFailure || d.IsNotSubscribed => false,
        DhanApiException d => d.HttpStatus is (int)HttpStatusCode.TooManyRequests or >= 500
                              || d.Code is "800" or "805" or "DH-904" or "DH-908" or "DH-909",
        HttpRequestException or IOException => true,
        // HttpClient's own timeout surfaces as a cancellation nobody asked for.
        OperationCanceledException => true,
        _ => false,
    };

    private static NpgsqlParameter Array<T>(string name, NpgsqlDbType type, T[] values) =>
        new(name, NpgsqlDbType.Array | type) { Value = values };
}
