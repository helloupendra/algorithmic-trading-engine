using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlgoTrading.Infrastructure.Services.OptionHistory;

/// <summary>
/// The index option expiries the exchanges actually recorded, from August 2020 on:
/// <c>SeedData/index_option_expiries.json</c>, built by
/// <c>tools/option_expiry_calendar.py</c> from the NSE and BSE derivative bhavcopies.
/// </summary>
/// <remarks>
/// The instrument master only knows contracts that are still listed, so a backtest
/// of 2021 has no way to tell which expiry was nearest on a given day. Weekday
/// rules would get it wrong around holidays, the 2023–2025 expiry-day changes and
/// one-off revisions. This calendar is what the exchanges' own files say: a date is
/// an expiry when that day's bhavcopy lists an index option expiring on it.
/// The file is read on first use, so a process that never asks pays nothing.
/// </remarks>
public sealed class IndexOptionExpiryCalendar
{
    public const string FileName = "index_option_expiries.json";

    private readonly Lazy<IReadOnlyDictionary<string, DateOnly[]>> _expiries;

    public IndexOptionExpiryCalendar(IHostEnvironment environment, ILogger<IndexOptionExpiryCalendar> logger)
    {
        string path = Path.Combine(environment.ContentRootPath, "SeedData", FileName);
        _expiries = new Lazy<IReadOnlyDictionary<string, DateOnly[]>>(() => Load(path, logger));
    }

    private IndexOptionExpiryCalendar(IReadOnlyDictionary<string, DateOnly[]> expiries)
    {
        _expiries = new Lazy<IReadOnlyDictionary<string, DateOnly[]>>(() => expiries);
    }

    /// <summary>A calendar over the given dates, for tests.</summary>
    public static IndexOptionExpiryCalendar From(IDictionary<string, IEnumerable<DateOnly>> expiries) =>
        new(expiries.ToDictionary(x => x.Key.ToUpperInvariant(), x => x.Value.Distinct().Order().ToArray(),
            StringComparer.OrdinalIgnoreCase));

    /// <summary>Expiries of one underlying, ascending; empty when the calendar has none.</summary>
    public IReadOnlyList<DateOnly> For(string underlying) =>
        _expiries.Value.TryGetValue(underlying.Trim(), out var dates) ? dates : Array.Empty<DateOnly>();

    internal static IReadOnlyDictionary<string, DateOnly[]> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var result = new Dictionary<string, DateOnly[]>(StringComparer.OrdinalIgnoreCase);
        if (!document.RootElement.TryGetProperty("underlyings", out var underlyings)) return result;
        foreach (var entry in underlyings.EnumerateObject())
        {
            result[entry.Name.ToUpperInvariant()] = entry.Value.EnumerateArray()
                .Select(x => DateOnly.ParseExact(x.GetString()!, "yyyy-MM-dd"))
                .Distinct()
                .Order()
                .ToArray();
        }
        return result;
    }

    private static IReadOnlyDictionary<string, DateOnly[]> Load(string path, ILogger logger)
    {
        try
        {
            if (!File.Exists(path))
            {
                logger.LogWarning("Index option expiry calendar not found at {Path}; backtests cannot price expired index options.", path);
                return new Dictionary<string, DateOnly[]>();
            }
            var calendar = Parse(File.ReadAllText(path));
            logger.LogInformation("Index option expiry calendar loaded: {Summary}",
                string.Join(", ", calendar.Select(x => $"{x.Key} {x.Value.Length}")));
            return calendar;
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException)
        {
            logger.LogError(ex, "Index option expiry calendar at {Path} could not be read.", path);
            return new Dictionary<string, DateOnly[]>();
        }
    }
}
