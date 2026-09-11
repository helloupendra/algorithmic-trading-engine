using System.Globalization;
using AlgoTrading.Application.Interfaces;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Providers.TrueData;

/// <summary>What one import pass did.</summary>
public sealed record TrueDataImportResult(
    string Segment,
    int RowsRead,
    int Mapped,
    int Skipped,
    string? Error = null);

/// <summary>
/// Downloads TrueData's symbol masters and records what it calls each contract.
/// </summary>
/// <remarks>
/// Run it once a day, before the open. TrueData's own advice is the same, and
/// for the same reason: the masters are large and change once a session.
///
/// <para>Only derivatives are mapped. Indices and cash equities translate by
/// their grammar already, so writing rows for them would be a second, staler
/// answer to a question that has one.</para>
/// </remarks>
public sealed class TrueDataSymbolImporter
{
    /// <summary>
    /// Segments worth importing: F&amp;O and MCX. The rest are derivable, and
    /// "all" would download a list TrueData itself calls not recommended.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultSegments = new[] { "fo", "mcx", "bsefo" };

    /// <summary>
    /// Ceiling asked of the vendor. High enough to mean "all of it" — the
    /// largest segment is under 80,000 rows — and present only because leaving
    /// it out silently caps the answer at twenty.
    /// </summary>
    public const int MaxRowsPerSegment = 200_000;

    private readonly IBrokerCredentialsProvider _credentials;
    private readonly ISymbolMapper _symbolMapper;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TrueDataSettings _settings;
    private readonly ILogger<TrueDataSymbolImporter> _logger;

    public TrueDataSymbolImporter(
        IOptions<TrueDataSettings> settings,
        IBrokerCredentialsProvider credentials,
        ISymbolMapper symbolMapper,
        IHttpClientFactory httpClientFactory,
        ILogger<TrueDataSymbolImporter> logger)
    {
        _settings = settings.Value;
        _credentials = credentials;
        _symbolMapper = symbolMapper;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TrueDataImportResult>> ImportAsync(
        IReadOnlyList<string>? segments = null,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<TrueDataImportResult>();

        foreach (var segment in segments ?? DefaultSegments)
        {
            try
            {
                results.Add(await ImportSegmentAsync(segment, search, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One segment failing must not cost the others: MCX is useful
                // even on a day the BSE list is unavailable.
                _logger.LogWarning(ex, "TrueData symbol import failed for segment {Segment}.", segment);
                results.Add(new TrueDataImportResult(segment, 0, 0, 0, ex.Message));
            }
        }

        return results;
    }

    private async Task<TrueDataImportResult> ImportSegmentAsync(
        string segment, string? search, CancellationToken cancellationToken)
    {
        var creds = await _credentials.GetAsync(TrueDataProvider.Key, cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(creds.ClientId) || string.IsNullOrWhiteSpace(creds.SecretKey))
            throw new InvalidOperationException("TrueData has no credentials saved.");

        // This host authenticates with the username and password in the query
        // string rather than the bearer token — TrueData's choice, not ours.
        var query = new Dictionary<string, string?>
        {
            ["segment"] = segment,
            ["user"] = creds.ClientId,
            ["password"] = creds.SecretKey,
            ["csv"] = "true",
            ["csvHeader"] = "true",
            // Expired contracts would map canonical names onto instruments that
            // can no longer be quoted.
            ["allexpiry"] = "false",
            // Without a limit TrueData answers with twenty rows and no hint
            // that it truncated: the first import looked like a success and
            // mapped a NIFTY chain's worth of nothing. Asked for explicitly,
            // the same call returns 7,122 rows for a NIFTY search and 79,380
            // for the whole F&O segment.
            ["limit"] = MaxRowsPerSegment.ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(search)) query["search"] = search;

        string url = QueryHelpers.AddQueryString(
            $"{_settings.SymbolApiBaseUrl.TrimEnd('/')}/getAllSymbols", query);

        var http = _httpClientFactory.CreateClient(TrueDataProvider.Key);
        using var response = await http.GetAsync(url, cancellationToken);
        string csv = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"TrueData symbol master failed ({(int)response.StatusCode}): " +
                (csv.Length > 200 ? csv[..200] : csv));
        }

        var rows = TrueDataSymbolMaster.ParseCsv(csv);
        int mapped = 0, skipped = 0;

        foreach (var row in rows)
        {
            string? canonical = TrueDataSymbolMaster.ToCanonical(row);
            if (canonical is null) { skipped++; continue; }

            await _symbolMapper.MapAsync(canonical, TrueDataProvider.Key, row.VendorSymbol, cancellationToken);
            mapped++;
        }

        _logger.LogInformation(
            "TrueData symbol master {Segment}: {Rows} row(s) read, {Mapped} mapped, {Skipped} skipped.",
            segment, rows.Count, mapped, skipped);

        return new TrueDataImportResult(segment, rows.Count, mapped, skipped);
    }
}
