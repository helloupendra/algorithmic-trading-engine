using System.Globalization;
using System.Text;
using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services.MutualFunds.Analytics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace AlgoTrading.Infrastructure.Services.MutualFunds;

/// <summary>
/// Keeps <c>mf_benchmark_tri</c> current from niftyindices.com: every night the last few weeks of each collected
/// index (<see cref="MutualFundBenchmarks.Indices"/>), and the first time each index's history back to 2006, a year
/// a request, newest first, paced.
/// </summary>
/// <remarks>
/// <para>
/// The source is the request niftyindices.com's own historical-data page makes:
/// <c>POST /BackPage/getTotalReturnIndexString</c> with <c>{"cinfo":"{'name':'NIFTY 50','startDate':'01-Oct-2025','endDate':'30-Sep-2026','indexName':'NIFTY 50'}"}</c>,
/// answered with a JSON array, newest first: <c>[{"Index Name":"Nifty 50","Date":"30 Oct 2026","TotalReturnsIndex":"35043.45",...}]</c>.
/// The page allows a year a request, and so does this. A range with no values is <c>[]</c>.
/// </para>
/// <para>
/// History is fetched once per index (about 21 requests from today to 2006, under a second each, 2.5 s apart) and
/// resumes where it stopped (<see cref="MutualFundBenchmarkHistory.Cursor"/>). A window that comes back empty ends the
/// index's history there: the index did not exist yet.
/// </para>
/// </remarks>
public sealed class MutualFundBenchmarkSync
{
    public const int WindowDays = 365;

    /// <summary>The nightly run rereads this many days before the last stored value, for late corrections.</summary>
    public const int RecentDays = 10;

    private readonly TradingDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IProcessSettingsStore _settings;
    private readonly MutualFundsOptions _options;
    private readonly ILogger<MutualFundBenchmarkSync> _logger;

    public MutualFundBenchmarkSync(TradingDbContext db, IHttpClientFactory http, IProcessSettingsStore settings, IOptions<MutualFundsOptions> options,
        ILogger<MutualFundBenchmarkSync> logger)
    {
        _db = db;
        _http = http;
        _settings = settings;
        _options = options.Value;
        _logger = logger;
    }

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>The pause before each request; replaced in tests.</summary>
    public Func<TimeSpan, CancellationToken, Task> Sleep { get; set; } = Task.Delay;

    /// <summary>
    /// Fetches the recent values of every index, then whatever history is still missing while
    /// <paramref name="mayContinue"/> allows (asked before every request). Never throws for the source's or the
    /// database's faults: the run says what happened.
    /// </summary>
    public async Task<MutualFundJobRun> RunAsync(string trigger, Func<bool> mayContinue, CancellationToken ct)
    {
        var started = Clock();
        var today = IstTime.DateOf(started);
        int written = 0;
        var errors = new List<string>();
        var stopped = false;

        foreach (var index in MutualFundBenchmarks.Indices)
        {
            if (!mayContinue())
            {
                stopped = true;
                break;
            }

            try
            {
                var last = await _db.MutualFundBenchmarkValues.AsNoTracking().Where(v => v.IndexName == index.Key).MaxAsync(v => (DateOnly?)v.Date, ct);
                var from = last is { } day ? day.AddDays(-RecentDays) : today.AddDays(-(WindowDays - 1));
                written += await FetchAndStoreAsync(index.Key, from, today, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                errors.Add($"{index.Key}: {ex.Message}");
                _logger.LogWarning("Benchmark sync: {Index} failed: {Error}", index.Key, ex.Message);
            }
        }

        if (!stopped && _options.BenchmarkBackfillEnabled)
        {
            foreach (var index in MutualFundBenchmarks.Indices)
            {
                var (rows, error, interrupted) = await HistoryAsync(index.Key, mayContinue, ct);
                written += rows;
                if (error is not null) errors.Add($"{index.Key} history: {error}");
                if (interrupted)
                {
                    stopped = true;
                    break;
                }
            }
        }

        var coverage = await CoverageAsync(ct);
        var reached = coverage.Count == MutualFundBenchmarks.Indices.Count && coverage.All(c => c.LastDate is not null)
            ? coverage.Min(c => c.LastDate)
            : null;
        var state = await MutualFundAnalyticsSettings.ReadAsync(_settings, ct);
        int complete = MutualFundBenchmarks.Indices.Count(i => state.BenchmarkHistory.TryGetValue(i.Key, out var h) && h.Complete);

        string message = $"{MutualFundBenchmarks.Indices.Count} indices; {written:N0} values written; "
                         + (reached is null ? "some indices have no values yet" : $"all have values to {FundAnalytics.Day(reached.Value)}")
                         + $"; history complete for {complete} of {MutualFundBenchmarks.Indices.Count}"
                         + (stopped ? "; stopped for market hours, it carries on after the close" : string.Empty)
                         + (errors.Count > 0 ? ". Failed: " + string.Join("; ", errors) : ".");
        var run = new MutualFundJobRun
        {
            Trigger = trigger,
            StartedUtc = started,
            FinishedUtc = Clock(),
            Ok = errors.Count == 0,
            Message = MutualFundAnalyticsSettings.Clip(message),
            Reached = reached,
            Rows = written,
        };
        _logger.LogInformation("Benchmark sync ({Trigger}): {Message}", trigger, run.Message);
        return run;
    }

    /// <summary>One index's missing history, newest window first, until it is complete or <paramref name="mayContinue"/> says stop.</summary>
    private async Task<(int Rows, string? Error, bool Interrupted)> HistoryAsync(string index, Func<bool> mayContinue, CancellationToken ct)
    {
        var state = await MutualFundAnalyticsSettings.ReadAsync(_settings, ct);
        var history = state.BenchmarkHistory.GetValueOrDefault(index) ?? new MutualFundBenchmarkHistory();
        if (history.Complete) return (0, null, false);

        if (history.Cursor is null)
        {
            var first = await _db.MutualFundBenchmarkValues.AsNoTracking().Where(v => v.IndexName == index).MinAsync(v => (DateOnly?)v.Date, ct);
            if (first is null) return (0, null, false);
            history.Cursor = first;
        }

        int written = 0;
        while (!history.Complete)
        {
            if (!mayContinue()) return (written, null, true);
            var end = history.Cursor!.Value.AddDays(-1);
            if (end < _options.BenchmarkFrom)
            {
                history.Complete = true;
                history.Note = $"history from {FundAnalytics.Day(_options.BenchmarkFrom)}";
                break;
            }

            var start = end.AddDays(-(WindowDays - 1));
            if (start < _options.BenchmarkFrom) start = _options.BenchmarkFrom;
            try
            {
                await Sleep(TimeSpan.FromSeconds(Math.Max(0, _options.RequestPaceSeconds)), ct);
                var values = await FetchAsync(index, start, end, ct);
                if (values.Count == 0)
                {
                    history.Complete = true;
                    history.Note = $"no values before {FundAnalytics.Day(history.Cursor.Value)}";
                    break;
                }

                written += await StoreAsync(index, values, ct);
                history.Cursor = start;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await SaveHistoryAsync(index, history, ct);
                return (written, ex.Message, false);
            }

            await SaveHistoryAsync(index, history, ct);
        }

        await SaveHistoryAsync(index, history, ct);
        _logger.LogInformation("Benchmark history: {Index} {Note}.", index, history.Note);
        return (written, null, false);
    }

    private Task SaveHistoryAsync(string index, MutualFundBenchmarkHistory history, CancellationToken ct) =>
        MutualFundAnalyticsSettings.UpdateAsync(_settings, s => s.BenchmarkHistory[index] = history, ct);

    private async Task<int> FetchAndStoreAsync(string index, DateOnly from, DateOnly to, CancellationToken ct)
    {
        int written = 0;
        for (var start = from; start <= to; start = start.AddDays(WindowDays))
        {
            var end = start.AddDays(WindowDays - 1);
            if (end > to) end = to;
            await Sleep(TimeSpan.FromSeconds(Math.Max(0, _options.RequestPaceSeconds)), ct);
            written += await StoreAsync(index, await FetchAsync(index, start, end, ct), ct);
        }

        return written;
    }

    /// <summary>One request: the index's values between two days, oldest first.</summary>
    public async Task<List<(DateOnly Date, decimal Value)>> FetchAsync(string index, DateOnly from, DateOnly to, CancellationToken ct)
    {
        string cinfo = string.Create(CultureInfo.InvariantCulture,
            $"{{'name':'{index}','startDate':'{from:dd-MMM-yyyy}','endDate':'{to:dd-MMM-yyyy}','indexName':'{index}'}}");
        using var body = new StringContent(JsonSerializer.Serialize(new { cinfo }), Encoding.UTF8, "application/json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        using var response = await _http.CreateClient(MutualFundsOptions.IndicesHttpClientName).PostAsync(_options.BenchmarkUrl, body, deadline.Token);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(deadline.Token), index);
    }

    /// <summary>
    /// niftyindices.com's answer: an array of rows (or the same array as a string in <c>d</c>, the older form).
    /// Rows for another index, without a date, or without a positive value are skipped. Oldest first.
    /// </summary>
    /// <exception cref="FormatException">The answer is not the expected JSON (an error page).</exception>
    public static List<(DateOnly Date, decimal Value)> Parse(string json, string index)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"niftyindices.com did not answer with JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.String)
                return Parse(d.GetString() ?? "[]", index);
            if (root.ValueKind != JsonValueKind.Array)
            {
                string message = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Message", out var m) ? m.GetString() ?? "" : root.ValueKind.ToString();
                throw new FormatException($"niftyindices.com answered without values: {message}");
            }

            string wanted = MutualFundNames.Key(index);
            var byDate = new SortedDictionary<DateOnly, decimal>();
            foreach (var row in root.EnumerateArray())
            {
                if (row.TryGetProperty("Index Name", out var name) && MutualFundNames.Key(name.GetString()) != wanted) continue;
                if (!row.TryGetProperty("Date", out var date) || !row.TryGetProperty("TotalReturnsIndex", out var value)) continue;
                if (!DateOnly.TryParseExact(date.GetString()?.Trim(), ["dd MMM yyyy", "d MMM yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
                string? text = value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.GetString();
                if (!decimal.TryParse(text?.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var level) || level <= 0) continue;
                byDate[day] = level;
            }

            return byDate.Select(p => (p.Key, p.Value)).ToList();
        }
    }

    /// <summary>Upserts values; a day already stored with the same value is not rewritten. Returns the rows written.</summary>
    private async Task<int> StoreAsync(string index, List<(DateOnly Date, decimal Value)> values, CancellationToken ct)
    {
        if (values.Count == 0) return 0;
        return await _db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO mf_benchmark_tri AS t ("IndexName", "Date", "Value")
            SELECT @index, d, v FROM unnest(@dates, @values) AS u(d, v)
            ON CONFLICT ("IndexName", "Date") DO UPDATE SET "Value" = EXCLUDED."Value"
            WHERE t."Value" <> EXCLUDED."Value"
            """,
            [
                new NpgsqlParameter("index", index),
                new NpgsqlParameter("dates", NpgsqlDbType.Array | NpgsqlDbType.Date) { Value = values.Select(v => v.Date).ToArray() },
                new NpgsqlParameter("values", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = values.Select(v => v.Value).ToArray() },
            ],
            ct);
    }

    /// <summary>What is stored of each collected index.</summary>
    public async Task<List<(string Index, int Rows, DateOnly? FirstDate, DateOnly? LastDate)>> CoverageAsync(CancellationToken ct)
    {
        var stored = await _db.MutualFundBenchmarkValues.AsNoTracking()
            .GroupBy(v => v.IndexName)
            .Select(g => new { Index = g.Key, Rows = g.Count(), First = g.Min(v => v.Date), Last = g.Max(v => v.Date) })
            .ToListAsync(ct);
        return MutualFundBenchmarks.Indices.Select(i =>
        {
            var s = stored.FirstOrDefault(x => x.Index == i.Key);
            return (i.Key, s?.Rows ?? 0, s is null ? (DateOnly?)null : s.First, s is null ? (DateOnly?)null : s.Last);
        }).ToList();
    }
}
