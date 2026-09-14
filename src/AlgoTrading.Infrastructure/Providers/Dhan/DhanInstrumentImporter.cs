using System.Diagnostics;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Providers.Dhan;

/// <summary>What one instrument import did.</summary>
public sealed record DhanImportResult(
    int MasterRows,
    int PlatformInstruments,
    int Matched,
    int Added,
    int Updated,
    int Removed,
    int Unmatched,
    IReadOnlyList<string> UnmatchedSample,
    IReadOnlyDictionary<string, int> MatchedByExchange,
    long DurationMs);

/// <summary>
/// Downloads Dhan's instrument master and records Dhan's segment and security id
/// for every live platform instrument it lists, in instrument_vendor_symbols.
/// </summary>
/// <remarks>
/// Run once a day before the open: contracts are listed and expire daily. The
/// table is written in bulk rather than a row at a time, because a full F&amp;O
/// universe is about two hundred thousand contracts. Rows for contracts that are no
/// longer listed are removed, so a mapping never points at an expired id.
/// </remarks>
public sealed class DhanInstrumentImporter
{
    private const int BatchSize = 5000;

    private readonly DhanSettings _settings;
    private readonly TradingDbContext _dbContext;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DhanInstrumentImporter> _logger;

    public DhanInstrumentImporter(
        IOptions<DhanSettings> settings,
        TradingDbContext dbContext,
        IHttpClientFactory httpClientFactory,
        ILogger<DhanInstrumentImporter> logger)
    {
        _settings = settings.Value;
        _dbContext = dbContext;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<DhanImportResult> ImportAsync(CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var today = IstTime.DateOf(DateTime.UtcNow);

        var rows = await DownloadAsync(today, cancellationToken);

        var instruments = await _dbContext.Instruments.AsNoTracking()
            .Where(x => x.Exchange == "NSE" || x.Exchange == "BSE" || x.Exchange == "MCX")
            .Where(x => (x.ExpiryDate != null && x.ExpiryDate >= today) || (x.Exchange == "NSE" && x.InstrumentType == "EQ"))
            .Select(x => new PlatformInstrument(x.Id, x.Symbol, x.Exchange, x.Segment, x.InstrumentType, x.Underlying, x.ExpiryDate, x.StrikePrice, x.OptionType))
            .ToListAsync(cancellationToken);

        var match = DhanInstrumentMaster.Match(rows, instruments);

        var existing = await _dbContext.InstrumentVendorSymbols
            .Where(x => x.ProviderKey == DhanProvider.Key)
            .ToListAsync(cancellationToken);

        int updated = 0, removed = 0;
        var now = DateTime.UtcNow;
        var kept = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in existing)
        {
            if (!match.ByCanonical.TryGetValue(row.CanonicalSymbol, out var target))
            {
                _dbContext.InstrumentVendorSymbols.Remove(row);
                removed++;
                continue;
            }

            kept.Add(row.CanonicalSymbol);
            string vendor = target.Instrument.ToVendorSymbol();
            if (row.VendorSymbol != vendor || row.InstrumentId != target.InstrumentId)
            {
                row.VendorSymbol = vendor;
                row.InstrumentId = target.InstrumentId;
                row.UpdatedUtc = now;
                updated++;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        _dbContext.ChangeTracker.Clear();

        int added = 0;
        var pending = new List<InstrumentVendorSymbol>(BatchSize);
        foreach (var (canonical, target) in match.ByCanonical)
        {
            if (kept.Contains(canonical)) continue;

            pending.Add(new InstrumentVendorSymbol
            {
                ProviderKey = DhanProvider.Key,
                CanonicalSymbol = canonical,
                VendorSymbol = target.Instrument.ToVendorSymbol(),
                InstrumentId = target.InstrumentId,
                CreatedUtc = now,
                UpdatedUtc = now,
            });

            if (pending.Count == BatchSize)
            {
                added += await FlushAsync(pending, cancellationToken);
            }
        }

        added += await FlushAsync(pending, cancellationToken);

        var byExchange = match.ByCanonical.Keys
            .GroupBy(s => s.Split(':')[0])
            .ToDictionary(g => g.Key, g => g.Count());

        var result = new DhanImportResult(
            rows.Count,
            instruments.Count,
            match.ByCanonical.Count,
            added,
            updated,
            removed,
            match.Unmatched.Count,
            match.Unmatched.Take(20).ToList(),
            byExchange,
            clock.ElapsedMilliseconds);

        _logger.LogInformation(
            "Dhan instrument import: {Master} master row(s), {Platform} platform instrument(s), {Matched} matched ({Added} added, {Updated} updated, {Removed} removed), {Unmatched} unmatched, {Ms} ms.",
            result.MasterRows, result.PlatformInstruments, result.Matched, result.Added, result.Updated, result.Removed, result.Unmatched, result.DurationMs);

        return result;
    }

    private async Task<List<DhanMasterRow>> DownloadAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var http = _httpClientFactory.CreateClient(DhanProvider.Key);
        using var response = await http.GetAsync(_settings.InstrumentMasterUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Dhan instrument master download failed ({(int)response.StatusCode}).");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var lines = new List<string>(210_000);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length > 0) lines.Add(line);
        }

        return DhanInstrumentMaster.Parse(lines, today).ToList();
    }

    private async Task<int> FlushAsync(List<InstrumentVendorSymbol> pending, CancellationToken cancellationToken)
    {
        if (pending.Count == 0) return 0;
        _dbContext.InstrumentVendorSymbols.AddRange(pending);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _dbContext.ChangeTracker.Clear();
        int count = pending.Count;
        pending.Clear();
        return count;
    }
}
