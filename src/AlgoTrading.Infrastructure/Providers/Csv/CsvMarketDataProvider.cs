using System.Globalization;
using AlgoTrading.Application.Providers;
using AlgoTrading.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Providers.Csv;

/// <summary>
/// Reads OHLCV bars from a CSV file per symbol and resolution.
/// </summary>
/// <remarks>
/// Expected columns, header required, order free:
/// <c>timestamp,open,high,low,close,volume</c>. The timestamp may be an ISO-8601
/// instant or epoch seconds; anything without a zone is read as UTC, because a
/// bar with an ambiguous timestamp is worse than no bar at all.
/// </remarks>
public class CsvMarketDataProvider : IMarketDataProvider
{
    private readonly string _directory;
    private readonly ILogger _logger;

    /// <summary>
    /// Built per vendor rather than resolved from DI: each file-based vendor an
    /// operator adds is its own connector, with its own key and its own folder.
    /// </summary>
    public CsvMarketDataProvider(
        ProviderDescriptor descriptor,
        string directory,
        ILogger logger)
    {
        Descriptor = descriptor;
        _directory = directory;
        _logger = logger;
    }

    public ProviderDescriptor Descriptor { get; }

    /// <summary>
    /// "NSE:NIFTYBANK-INDEX" + "15" → "NSE_NIFTYBANK-INDEX__15.csv". Only the
    /// characters that cannot appear in a filename are replaced, so the symbol
    /// stays readable in a directory listing.
    /// </summary>
    internal static string FileNameFor(string canonicalSymbol, string resolution)
    {
        string safe = canonicalSymbol.Replace(':', '_').Replace('/', '_').Replace('\\', '_');
        return $"{safe}__{resolution}.csv";
    }

    public async Task<IReadOnlyList<ProviderHistoryBar>> GetHistoryAsync(
        string canonicalSymbol,
        string resolution,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(canonicalSymbol))
        {
            throw new ArgumentException("Symbol is required.", nameof(canonicalSymbol));
        }

        string storedResolution = ResolutionCodes.ToCandle(resolution);

        // Always resolve to an absolute path: the API's working directory is not
        // the repository root, so a relative one in the error message sends the
        // operator to the wrong folder.
        string path = Path.GetFullPath(
            Path.Combine(_directory, FileNameFor(canonicalSymbol, storedResolution)));

        if (!File.Exists(path))
        {
            // No file is this connector's way of saying "I do not carry that
            // symbol" — the same meaning a vendor gives an unknown symbol.
            throw new ProviderSymbolRejectedException(
                Descriptor.Key,
                canonicalSymbol,
                $"no file at {path}");
        }

        DateTime from = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        DateTime to = DateTime.SpecifyKind(toUtc, DateTimeKind.Utc);

        var bars = new List<ProviderHistoryBar>();
        int lineNumber = 0;
        int malformed = 0;
        Dictionary<string, int>? columns = null;

        using var reader = new StreamReader(path);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;

            if (string.IsNullOrWhiteSpace(line)) continue;

            var cells = line.Split(',');

            if (columns is null)
            {
                columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < cells.Length; i++)
                {
                    columns[cells[i].Trim()] = i;
                }

                foreach (var required in new[] { "timestamp", "open", "high", "low", "close" })
                {
                    if (!columns.ContainsKey(required))
                    {
                        throw new InvalidOperationException(
                            $"{path} has no '{required}' column. Expected header: timestamp,open,high,low,close,volume.");
                    }
                }

                continue;
            }

            if (!TryParseRow(cells, columns, out var bar))
            {
                malformed++;
                continue;
            }

            if (bar.TimestampUtc < from || bar.TimestampUtc > to) continue;

            bars.Add(bar);
        }

        if (malformed > 0)
        {
            // Say it out loud. A silently dropped row is a hole in a backtest that
            // nobody would ever go looking for.
            _logger.LogWarning(
                "{Path}: skipped {Malformed} unparseable row(s) of {Total}.",
                path, malformed, lineNumber - 1);
        }

        bars.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));

        return bars;
    }

    private static bool TryParseRow(
        string[] cells,
        Dictionary<string, int> columns,
        out ProviderHistoryBar bar)
    {
        bar = new ProviderHistoryBar();

        if (!TryCell(cells, columns, "timestamp", out string rawTimestamp) ||
            !TryDecimal(cells, columns, "open", out decimal open) ||
            !TryDecimal(cells, columns, "high", out decimal high) ||
            !TryDecimal(cells, columns, "low", out decimal low) ||
            !TryDecimal(cells, columns, "close", out decimal close))
        {
            return false;
        }

        if (!TryTimestamp(rawTimestamp, out DateTime timestampUtc))
        {
            return false;
        }

        TryDecimal(cells, columns, "volume", out decimal volume);

        bar = new ProviderHistoryBar
        {
            TimestampUtc = timestampUtc,
            Open = open,
            High = high,
            Low = low,
            Close = close,
            Volume = volume,

            // A CSV of OHLCV carries no open interest; null, never a zero that a
            // strategy could mistake for real data.
            OpenInterest = null,
        };

        return true;
    }

    private static bool TryCell(
        string[] cells,
        Dictionary<string, int> columns,
        string name,
        out string value)
    {
        value = string.Empty;

        if (!columns.TryGetValue(name, out int index) || index >= cells.Length)
        {
            return false;
        }

        value = cells[index].Trim();
        return value.Length > 0;
    }

    private static bool TryDecimal(
        string[] cells,
        Dictionary<string, int> columns,
        string name,
        out decimal value)
    {
        value = 0m;

        return TryCell(cells, columns, name, out string raw) &&
               decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Epoch seconds or ISO-8601; a naive timestamp is read as UTC.</summary>
    private static bool TryTimestamp(string raw, out DateTime timestampUtc)
    {
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long epochSeconds))
        {
            timestampUtc = DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;
            return true;
        }

        if (DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            timestampUtc = parsed.UtcDateTime;
            return true;
        }

        timestampUtc = default;
        return false;
    }
}
