using System.Globalization;
using System.Text;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Services.MutualFunds;

/// <summary>What one history request read and wrote.</summary>
public sealed record HistoryFetch(int SchemeType, DateOnly From, DateOnly To, int Rows, int RowsWithoutNav, int NavRowsWritten, bool NoData);

/// <summary>
/// Fills <c>mf_nav</c> from AMFI's NAV history download, newest first, one
/// 90-day window at a time, all schemes of a type per request.
/// </summary>
/// <remarks>
/// <para>
/// The download is <c>DownloadNAVHistoryReport_Po.aspx?tp=&amp;frmdt=&amp;todt=</c>
/// with <c>tp</c> 1 for open-ended schemes, 2 for close-ended and 3 for
/// interval funds, and dates like 01-Apr-2020. The history starts on 1 Apr
/// 2006. A window of open-ended schemes is 40–80 MB of text (gzipped on the
/// wire) and AMFI takes about two minutes to produce it, so a window is read
/// as a stream and written 5,000 NAVs at a time; memory stays flat.
/// </para>
/// <para>
/// When AMFI has nothing for a range it answers HTTP 200 with its download
/// form. That is normal for close-ended and interval funds in some windows,
/// but open-ended schemes have NAVs in every window since 2006, so an empty
/// open-ended window is treated as a failure, never as a gap.
/// </para>
/// <para>
/// Resumable by window: the state (<see cref="MutualFundBackfillState"/>)
/// records the first day of the oldest window finished, after all three
/// types are in, so a run stopped anywhere repeats at most one window, and
/// repeating it writes nothing new (the NAV upsert skips equal values).
/// Requests are paced (<see cref="MutualFundsOptions.RequestPaceSeconds"/>)
/// and run one at a time.
/// </para>
/// </remarks>
public sealed class MutualFundHistory
{
    public static readonly DateOnly Earliest = new(2006, 4, 1);
    public const int WindowDays = 90;

    /// <summary>AMFI's <c>tp</c>: open-ended, close-ended, interval.</summary>
    public static readonly IReadOnlyList<int> SchemeTypes = [1, 2, 3];

    private static readonly TimeSpan[] RetryAfter = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    /// <summary>After a request fails for good, the backfill waits this long before it tries the window again.</summary>
    public static readonly TimeSpan FailedPause = TimeSpan.FromHours(1);

    /// <summary>Whether a requested backfill may run now: not within <see cref="FailedPause"/> of a failure.</summary>
    public static bool ReadyToRun(MutualFundBackfillState state, DateTime nowUtc) =>
        state.Requested && !(state.State == "failed" && state.UpdatedUtc is { } at && nowUtc - at < FailedPause);

    private readonly MutualFundStore _store;
    private readonly IHttpClientFactory _http;
    private readonly IProcessSettingsStore _settings;
    private readonly MutualFundStatus _status;
    private readonly MutualFundsOptions _options;
    private readonly ILogger<MutualFundHistory> _logger;

    public MutualFundHistory(MutualFundStore store, IHttpClientFactory http, IProcessSettingsStore settings, MutualFundStatus status,
        IOptions<MutualFundsOptions> options, ILogger<MutualFundHistory> logger)
    {
        _store = store;
        _http = http;
        _settings = settings;
        _status = status;
        _options = options.Value;
        _logger = logger;
    }

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>The waits between attempts; replaced in tests.</summary>
    public Func<TimeSpan, CancellationToken, Task> Sleep { get; set; } = Task.Delay;

    public static string Url(string baseUrl, int schemeType, DateOnly from, DateOnly to) =>
        string.Create(CultureInfo.InvariantCulture, $"{baseUrl}?tp={schemeType}&frmdt={from:dd-MMM-yyyy}&todt={to:dd-MMM-yyyy}");

    /// <summary>
    /// Carries the backfill on from its saved state, window after window, until
    /// it reaches <see cref="MutualFundBackfillState.From"/>, a request fails
    /// three times, <see cref="MutualFundBackfillState.MaxWindows"/> windows are
    /// done, or <paramref name="mayContinue"/> says stop (asked before every request).
    /// </summary>
    public async Task<MutualFundBackfillState> RunAsync(Func<bool> mayContinue, CancellationToken ct)
    {
        var state = await MutualFundSettings.BackfillAsync(_settings, ct);
        if (!state.Requested) return state;

        state.Top ??= IstTime.DateOf(Clock()).AddDays(-1);
        state.StartedUtc ??= Clock();
        state.State = "running";
        state.LastError = null;
        await SaveAsync(state, ct);

        _status.BackfillRunning = true;
        int windowsThisRun = 0;
        try
        {
            while (state.NextWindow() is { } window)
            {
                if (!mayContinue()) return await StopAsync(state, _status.StopBackfill ? "paused" : "waiting", ct);
                if (state.MaxWindows is { } max && windowsThisRun >= max)
                {
                    state.Requested = false;
                    return await StopAsync(state, "paused", ct);
                }

                long written = 0;
                foreach (int type in SchemeTypes)
                {
                    if (!mayContinue()) return await StopAsync(state, _status.StopBackfill ? "paused" : "waiting", ct);

                    var fetch = await FetchWithRetriesAsync(type, window.From, window.To, ct);
                    if (fetch is null)
                    {
                        // Still requested: the service tries again after FailedPause, so a
                        // night when AMFI is down costs an hour, not the backfill.
                        state.State = "failed";
                        state.LastError = LastFailure;
                        await SaveAsync(state, ct);
                        _logger.LogWarning("Mutual fund history backfill stopped at {From}..{To}: {Error}", window.From, window.To, state.LastError);
                        return state;
                    }

                    written += fetch.NavRowsWritten;
                }

                state.Cursor = window.From;
                state.WindowsDone++;
                state.RowsWritten += written;
                state.LastWindow = $"{window.From:yyyy-MM-dd}..{window.To:yyyy-MM-dd}";
                windowsThisRun++;
                await SaveAsync(state, ct);
                _logger.LogInformation("Mutual fund history: {Window} stored, {Rows:N0} NAVs written ({Done} windows, {Total:N0} NAVs so far).",
                    state.LastWindow, written, state.WindowsDone, state.RowsWritten);
                await CompressAsync(window.From, ct);
            }

            state.Requested = false;
            return await StopAsync(state, "done", ct);
        }
        finally
        {
            _status.BackfillRunning = false;
            _status.BackfillNow = null;
        }
    }

    /// <summary>Compresses the chunks finished so far; a failure is logged, never the end of the backfill (the policy job compresses them later).</summary>
    private async Task CompressAsync(DateOnly cursor, CancellationToken ct)
    {
        try
        {
            int compressed = await _store.CompressFinishedChunksAsync(cursor, ct);
            if (compressed > 0) _logger.LogInformation("Mutual fund history: compressed {Chunks} finished chunk(s) of mf_nav.", compressed);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Mutual fund history: could not compress finished chunks ({Error}); the compression policy will.", ex.Message);
        }
    }

    private async Task<MutualFundBackfillState> StopAsync(MutualFundBackfillState state, string how, CancellationToken ct)
    {
        state.State = how;
        if (how == "paused") state.Requested = false;
        await SaveAsync(state, ct);
        return state;
    }

    private Task SaveAsync(MutualFundBackfillState state, CancellationToken ct)
    {
        state.UpdatedUtc = Clock();
        return MutualFundSettings.SaveBackfillAsync(_settings, state, ct);
    }

    private async Task<HistoryFetch?> FetchWithRetriesAsync(int type, DateOnly from, DateOnly to, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await ImportAsync(type, from, to, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                string error = $"tp={type} {from:yyyy-MM-dd}..{to:yyyy-MM-dd}: {ex.Message}";
                if (attempt >= RetryAfter.Length)
                {
                    LastFailure = error;
                    return null;
                }

                _logger.LogWarning("Mutual fund history {Error}; trying again in {Seconds:N0}s.", error, RetryAfter[attempt].TotalSeconds);
                await Sleep(RetryAfter[attempt], ct);
            }
        }
    }

    /// <summary>The error of the request that failed for good, which ends a run.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>
    /// Reads one window of one scheme type and stores it: NAVs first, then the
    /// schemes they belong to (added to the master if new, unknown fields filled).
    /// </summary>
    /// <exception cref="Exception">AMFI or the database failed; nothing half-read is counted as done.</exception>
    public async Task<HistoryFetch> ImportAsync(int type, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _options.RequestPaceSeconds)), ct);
        await _status.WriteGate.WaitAsync(ct);
        _status.BackfillNow = $"tp={type} {from:yyyy-MM-dd}..{to:yyyy-MM-dd}";
        try
        {
            var counts = new AmfiParseCounts();
            int written = 0;
            bool noData = false;
            string url = Url(_options.HistoryUrl, type, from, to);

            using var response = await _http.CreateClient(MutualFundsOptions.HttpClientName)
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            try
            {
                var batch = new List<AmfiNavRow>(MutualFundStore.BatchSize);
                foreach (var row in AmfiNavParser.Parse(reader, counts))
                {
                    batch.Add(row);
                    if (batch.Count < MutualFundStore.BatchSize) continue;
                    written += await WriteBatchAsync(batch, ct);
                    batch.Clear();
                }

                if (batch.Count > 0) written += await WriteBatchAsync(batch, ct);
            }
            catch (AmfiNoDataException)
            {
                noData = true;
            }

            if (type == 1 && (noData || counts.Rows == 0))
                throw new InvalidDataException("AMFI returned no open-ended NAVs for a window that must have them.");

            return new HistoryFetch(type, from, to, counts.Rows, counts.RowsWithoutNav, written, noData);
        }
        finally
        {
            _status.WriteGate.Release();
        }
    }

    private async Task<int> WriteBatchAsync(List<AmfiNavRow> rows, CancellationToken ct)
    {
        var navs = rows
            .Where(r => r is { Nav: not null, Date: not null })
            .Select(r => new MutualFundNav { SchemeCode = r.SchemeCode, Date = r.Date!.Value, Nav = r.Nav!.Value, Source = MutualFundNavSources.AmfiHistory })
            .ToList();
        int written = await _store.UpsertNavsAsync(navs, ct);
        await _store.UpsertSchemesAsync(SchemesOf(rows), authoritative: false, Clock(), ct);
        return written;
    }

    /// <summary>One merge row per scheme in a batch: its NAV date range there, and what its newest row says.</summary>
    public static List<SchemeUpsert> SchemesOf(IEnumerable<AmfiNavRow> rows) =>
        rows.GroupBy(r => r.SchemeCode)
            .Select(g =>
            {
                var dated = g.Where(r => r is { Nav: not null, Date: not null }).OrderBy(r => r.Date).ToList();
                var newest = dated.Count > 0 ? dated[^1] : g.Last();
                return new SchemeUpsert(g.Key, newest.IsinGrowth, newest.IsinReinvest, newest.Amc, newest.SchemeName,
                    newest.Plan, newest.Option, newest.OptionLabel, newest.Category,
                    dated.Count > 0 ? dated[0].Date : null,
                    dated.Count > 0 ? dated[^1].Date : null,
                    dated.Count > 0 ? dated[^1].Nav : null);
            })
            .ToList();
}
