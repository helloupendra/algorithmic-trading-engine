using System.Collections.Concurrent;
using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.Instruments;
using AlgoTrading.Contracts.Instruments;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Keeps the instrument universe current from FYERS's public symbol masters:
/// downloads each CSV into data/instruments and runs the same import the
/// import-local endpoint does, one master after another, in the background.
/// </summary>
/// <remarks>
/// Why a job and not a request: the five files are ~23 MB and the F&amp;O
/// import alone takes a minute, and the console reaches this API through a
/// tunnel that drops any request running longer than 100 seconds. The button
/// starts the job and the page polls its state. One job at a time — two would
/// import the same file twice.
/// <para>
/// The last result per master is kept in system settings, so the page still
/// says when a master was last refreshed after an API restart.
/// </para>
/// </remarks>
public sealed class InstrumentMasterService
{
    public const string BaseUrl = "https://public.fyers.in/sym_details/";
    /// <summary>The cache entry the underlyings picker reads; a refresh drops it.</summary>
    public const string FnoUnderlyingsCacheKey = "instruments:fno-underlyings";

    /// <summary>The masters, in the order they are refreshed (cash before F&amp;O: an option needs its underlying).</summary>
    public static readonly IReadOnlyList<(string Name, string Exchange, string Segment, string Label)> Known = new[]
    {
        ("NSE_CM",  "NSE", "CM",  "NSE cash: stocks and indices (NIFTY 50, NIFTY BANK, FINNIFTY)"),
        ("NSE_FO",  "NSE", "FO",  "NSE futures and options"),
        ("BSE_CM",  "BSE", "CM",  "BSE cash: stocks and indices (SENSEX, BANKEX)"),
        ("BSE_FO",  "BSE", "FO",  "BSE futures and options (SENSEX, BANKEX)"),
        ("MCX_COM", "MCX", "COM", "MCX commodities: futures and options (crude, gold, silver, natural gas…)"),
    };

    private const string SettingPrefix = "instruments.master.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _http;
    private readonly IServiceScopeFactory _scopes;
    private readonly IMemoryCache _cache;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ILogger<InstrumentMasterService> _logger;

    private readonly object _gate = new();
    private InstrumentMasterJob _job = new();
    private readonly ConcurrentDictionary<string, InstrumentMasterRefreshResult> _lastByName = new(StringComparer.OrdinalIgnoreCase);

    public InstrumentMasterService(
        IHttpClientFactory http,
        IServiceScopeFactory scopes,
        IMemoryCache cache,
        IWebHostEnvironment env,
        IConfiguration config,
        ILogger<InstrumentMasterService> logger)
    {
        _http = http;
        _scopes = scopes;
        _cache = cache;
        _env = env;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// data/instruments at the repo root (two levels above the API's content
    /// root), unless Instruments:MasterDirectory says otherwise. The same
    /// folder scripts/setup.sh downloads into.
    /// </summary>
    public string Directory
    {
        get
        {
            var configured = _config["Instruments:MasterDirectory"];
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
            return Path.GetFullPath(Path.Combine(_env.ContentRootPath, "..", "..", "data", "instruments"));
        }
    }

    public async Task<InstrumentMastersResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<IProcessSettingsStore>();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var counts = await db.Instruments.AsNoTracking()
            .GroupBy(x => new { x.Exchange, x.Segment })
            .Select(g => new
            {
                g.Key.Exchange,
                g.Key.Segment,
                Total = g.Count(),
                Active = g.Count(x => x.ExpiryDate == null || x.ExpiryDate >= today),
            })
            .ToListAsync(cancellationToken);

        var response = new InstrumentMastersResponse { Directory = Directory };
        foreach (var (name, exchange, segment, label) in Known)
        {
            var path = Path.Combine(Directory, name + ".csv");
            var info = new FileInfo(path);
            var count = counts.FirstOrDefault(c =>
                string.Equals(c.Exchange, exchange, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Segment, segment, StringComparison.OrdinalIgnoreCase));

            var last = _lastByName.TryGetValue(name, out var mem) ? mem : await ReadStoredAsync(settings, name, cancellationToken);

            response.Masters.Add(new InstrumentMasterStatus
            {
                Name = name,
                Exchange = exchange,
                Segment = segment,
                Label = label,
                Url = BaseUrl + name + ".csv",
                FilePresent = info.Exists,
                FileBytes = info.Exists ? info.Length : null,
                FileModifiedUtc = info.Exists ? info.LastWriteTimeUtc : null,
                RowsInDb = count?.Total ?? 0,
                ActiveRowsInDb = count?.Active ?? 0,
                LastRefresh = last,
            });
        }

        lock (_gate)
        {
            response.Job = Snapshot();
        }
        return response;
    }

    /// <summary>Starts the job for every known master. False when one is already running.</summary>
    public bool TryStart(string startedBy, IReadOnlyList<string>? only = null)
    {
        var names = Known.Select(k => k.Name)
            .Where(n => only is null || only.Count == 0 || only.Contains(n, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (names.Count == 0) return false;

        lock (_gate)
        {
            if (_job.IsRunning) return false;
            _job = new InstrumentMasterJob { IsRunning = true, StartedUtc = DateTime.UtcNow, StartedBy = startedBy };
        }
        _ = Task.Run(() => RunAsync(names, startedBy));
        return true;
    }

    private async Task RunAsync(IReadOnlyList<string> names, string startedBy)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            foreach (var name in names)
            {
                var result = new InstrumentMasterRefreshResult { Name = name, StartedUtc = DateTime.UtcNow, By = startedBy };
                lock (_gate) { _job.Current = name; _job.Results.Add(result); }
                try
                {
                    var path = Path.Combine(Directory, name + ".csv");
                    result.DownloadedBytes = await DownloadAsync(name, path);

                    using var scope = _scopes.CreateScope();
                    var import = scope.ServiceProvider.GetRequiredService<ImportInstrumentsFromFileUseCase>();
                    var imported = await import.ExecuteAsync(new ImportInstrumentsRequest { FilePath = path }, CancellationToken.None);
                    result.TotalRowsRead = imported.TotalRowsRead;
                    result.Inserted = imported.Inserted;
                    result.Updated = imported.Updated;
                    result.Skipped = imported.Skipped;
                    result.Message = imported.Message;
                    result.Ok = true;
                    _logger.LogInformation("Instrument master {Name} refreshed: {Rows} rows, {Inserted} inserted, {Updated} updated.",
                        name, imported.TotalRowsRead, imported.Inserted, imported.Updated);
                }
                catch (Exception ex)
                {
                    result.Ok = false;
                    result.Error = ex.Message;
                    _logger.LogError(ex, "Instrument master {Name} refresh failed.", name);
                }
                finally
                {
                    result.FinishedUtc = DateTime.UtcNow;
                    _lastByName[name] = result;
                    await StoreAsync(name, result);
                }
            }
            _cache.Remove(FnoUnderlyingsCacheKey);
        }
        finally
        {
            lock (_gate)
            {
                _job.IsRunning = false;
                _job.Current = null;
                _job.FinishedUtc = DateTime.UtcNow;
            }
        }
    }

    /// <summary>Downloads to a .part file and renames, so a failed download never replaces a good master.</summary>
    private async Task<long> DownloadAsync(string name, string path)
    {
        var client = _http.CreateClient(nameof(InstrumentMasterService));
        client.Timeout = TimeSpan.FromMinutes(5);
        var part = path + ".part";
        using (var response = await client.GetAsync(BaseUrl + name + ".csv", HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var src = await response.Content.ReadAsStreamAsync();
            await using var dst = File.Create(part);
            await src.CopyToAsync(dst);
        }
        var bytes = new FileInfo(part).Length;
        if (bytes < 1024) throw new InvalidOperationException($"{name}.csv came back with only {bytes} bytes — not a master; kept the previous file.");
        File.Move(part, path, overwrite: true);
        return bytes;
    }

    private InstrumentMasterJob Snapshot() => new()
    {
        IsRunning = _job.IsRunning,
        StartedUtc = _job.StartedUtc,
        FinishedUtc = _job.FinishedUtc,
        Current = _job.Current,
        StartedBy = _job.StartedBy,
        Results = _job.Results.Select(r => new InstrumentMasterRefreshResult
        {
            Name = r.Name, StartedUtc = r.StartedUtc, FinishedUtc = r.FinishedUtc, Ok = r.Ok, Error = r.Error,
            DownloadedBytes = r.DownloadedBytes, TotalRowsRead = r.TotalRowsRead, Inserted = r.Inserted,
            Updated = r.Updated, Skipped = r.Skipped, Message = r.Message, By = r.By,
        }).ToList(),
    };

    private async Task StoreAsync(string name, InstrumentMasterRefreshResult result)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IProcessSettingsStore>();
            await settings.SetAsync(SettingPrefix + name, JsonSerializer.Serialize(result, Json), result.By, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not store the refresh result of master {Name}.", name);
        }
    }

    private async Task<InstrumentMasterRefreshResult?> ReadStoredAsync(IProcessSettingsStore settings, string name, CancellationToken cancellationToken)
    {
        try
        {
            var raw = await settings.GetAsync(SettingPrefix + name, cancellationToken);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var stored = JsonSerializer.Deserialize<InstrumentMasterRefreshResult>(raw, Json);
            if (stored is not null) _lastByName[name] = stored;
            return stored;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
