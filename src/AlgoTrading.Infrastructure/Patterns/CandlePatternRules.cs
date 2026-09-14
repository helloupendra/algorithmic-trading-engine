using System.Globalization;
using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Infrastructure.Patterns;

/// <summary>A named set of symbols a rule can watch without listing them.</summary>
public sealed record PatternSymbolGroup(string Key, string Label, string Description);

/// <summary>The symbol groups a rule may name.</summary>
public static class PatternSymbolGroups
{
    public const string Indices = "indices";
    public const string IndexFutures = "index-futures";
    public const string RecordingStocks = "recording-stocks";
    public const string McxFutures = "mcx-futures";

    /// <summary>"future:CRUDEOIL" — one underlying's nearest-expiry future.</summary>
    public const string FuturePrefix = "future:";

    public static readonly IReadOnlyList<PatternSymbolGroup> Fixed =
    [
        new(Indices, "Indices", "NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, NIFTYNXT50, SENSEX and BANKEX spot, plus any index on the recording list."),
        new(IndexFutures, "Index futures", "The nearest-expiry future of each index, re-resolved every scan so an expiry rolls by itself."),
        new(RecordingStocks, "Stocks on the recording list", "Every active NSE/BSE equity (…-EQ) on the live recording list."),
        new(McxFutures, "MCX futures", "The nearest-expiry future of CRUDEOIL, CRUDEOILM, NATURALGAS, NATGASMINI, GOLD, GOLDM, SILVER and SILVERM."),
    ];

    /// <summary>Lower-cased fixed keys, and future groups with the underlying upper-cased.</summary>
    public static string? Normalize(string? group)
    {
        var g = (group ?? string.Empty).Trim();
        if (g.Length == 0) return null;
        if (g.StartsWith(FuturePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var underlying = g[FuturePrefix.Length..].Trim().ToUpperInvariant();
            return underlying.Length > 0 && underlying.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '&' or '-')
                ? FuturePrefix + underlying
                : null;
        }

        var key = g.ToLowerInvariant();
        return Fixed.Any(x => x.Key == key) ? key : null;
    }
}

/// <summary>A rule read out of its row: lists parsed, unknown entries dropped.</summary>
public sealed record ParsedPatternRule(
    long Id,
    string Name,
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string> Groups,
    IReadOnlyList<int> Timeframes,
    IReadOnlyList<CandlePattern> Patterns,
    bool IsEnabled,
    bool Notify);

/// <summary>Parsing, validation and the shipped defaults for <see cref="CandlePatternRule"/>.</summary>
public static class CandlePatternRules
{
    public const int MaxSymbols = 200;

    private static readonly string[] Exchanges = ["NSE", "BSE", "MCX"];

    public static IReadOnlyList<string> SplitCsv(string? csv) =>
        (csv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    /// <summary>
    /// Lenient: a row edited by hand into something unreadable loses the unreadable
    /// entries, not the whole rule. The API's validation keeps new rows clean.
    /// </summary>
    public static ParsedPatternRule Parse(CandlePatternRule row) => new(
        row.Id,
        row.Name,
        SplitCsv(row.SymbolsCsv).Select(NormalizeSymbol).OfType<string>().Distinct(StringComparer.Ordinal).ToList(),
        SplitCsv(row.GroupsCsv).Select(PatternSymbolGroups.Normalize).OfType<string>().Distinct(StringComparer.Ordinal).ToList(),
        SplitCsv(row.TimeframesCsv)
            .Select(t => int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : 0)
            .Where(m => SessionBarAggregator.SupportedTimeframes.Contains(m))
            .Distinct().Order().ToList(),
        SplitCsv(row.PatternsCsv)
            .Select(k => CandlePatternCatalog.TryParse(k, out var p) ? (CandlePattern?)p : null)
            .OfType<CandlePattern>().Distinct().Order().ToList(),
        row.IsEnabled,
        row.Notify);

    /// <summary>"nse:sbin-eq " → "NSE:SBIN-EQ"; null unless it is EXCHANGE:NAME on a known exchange.</summary>
    public static string? NormalizeSymbol(string? symbol)
    {
        var s = (symbol ?? string.Empty).Trim().ToUpperInvariant();
        int colon = s.IndexOf(':');
        if (colon <= 0 || colon == s.Length - 1 || s.Length > 100) return null;
        if (!Exchanges.Contains(s[..colon])) return null;
        return s[(colon + 1)..].All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '&' or '_') ? s : null;
    }

    /// <summary>The exchange prefix of a canonical symbol ("NSE" for "NSE:SBIN-EQ").</summary>
    public static string ExchangeOf(string symbol)
    {
        int colon = symbol.IndexOf(':');
        return colon > 0 ? symbol[..colon].ToUpperInvariant() : string.Empty;
    }

    /// <summary>Everything wrong with a rule as submitted, in words for the form. Empty when it can be saved.</summary>
    public static IReadOnlyList<string> Validate(
        string? name,
        IEnumerable<string>? symbols,
        IEnumerable<string>? groups,
        IEnumerable<int>? timeframes,
        IEnumerable<string>? patterns)
    {
        var errors = new List<string>();

        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is 0 or > 120) errors.Add("Name is required (at most 120 characters).");

        var symbolList = (symbols ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var badSymbols = symbolList.Where(s => NormalizeSymbol(s) is null).ToList();
        if (badSymbols.Count > 0)
            errors.Add($"Symbols must be EXCHANGE:NAME on NSE, BSE or MCX (for example NSE:SBIN-EQ): {string.Join(", ", badSymbols.Take(5))}.");
        if (symbolList.Count > MaxSymbols) errors.Add($"At most {MaxSymbols} explicit symbols.");

        var groupList = (groups ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
        var badGroups = groupList.Where(g => PatternSymbolGroups.Normalize(g) is null).ToList();
        if (badGroups.Count > 0)
            errors.Add($"Unknown symbol group: {string.Join(", ", badGroups)}. Use {string.Join(", ", PatternSymbolGroups.Fixed.Select(x => x.Key))} or future:UNDERLYING.");

        if (symbolList.Count == 0 && groupList.Count == 0) errors.Add("Pick at least one symbol or group.");

        var tfList = (timeframes ?? []).ToList();
        if (tfList.Count == 0) errors.Add("Pick at least one timeframe.");
        var badTf = tfList.Where(t => !SessionBarAggregator.SupportedTimeframes.Contains(t)).ToList();
        if (badTf.Count > 0)
            errors.Add($"Timeframes must be {string.Join(", ", SessionBarAggregator.SupportedTimeframes)} minutes, not {string.Join(", ", badTf)}.");

        var patternList = (patterns ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (patternList.Count == 0) errors.Add("Pick at least one pattern.");
        var badPatterns = patternList.Where(p => !CandlePatternCatalog.TryParse(p, out _)).ToList();
        if (badPatterns.Count > 0) errors.Add($"Unknown pattern: {string.Join(", ", badPatterns)}.");

        return errors;
    }

    /// <summary>
    /// The rules a fresh install starts with. Seeded once (see ReferenceDataSeeder);
    /// after that the table is the operator's, deletions included.
    /// </summary>
    public static IReadOnlyList<CandlePatternRule> Defaults(DateTime nowUtc)
    {
        string all = string.Join(',', CandlePatternCatalog.All.Select(x => x.Key));
        return
        [
            new CandlePatternRule
            {
                Name = "Indices — 15m, every pattern",
                GroupsCsv = PatternSymbolGroups.Indices,
                TimeframesCsv = "15",
                PatternsCsv = all,
                IsEnabled = true,
                Notify = true,
                UpdatedBy = "seed",
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
            },
            // On the page, not on Telegram: replayed over 8 Sep, indices on 5
            // minutes alone produced 177 alerts, a message about every five
            // minutes all session, which trains the reader to ignore the chat.
            new CandlePatternRule
            {
                Name = "Indices — 5m, every pattern (page only)",
                GroupsCsv = PatternSymbolGroups.Indices,
                TimeframesCsv = "5",
                PatternsCsv = all,
                IsEnabled = true,
                Notify = false,
                UpdatedBy = "seed",
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
            },
            new CandlePatternRule
            {
                Name = "Stocks on the recording list — 15m reversals",
                GroupsCsv = PatternSymbolGroups.RecordingStocks,
                TimeframesCsv = "15",
                PatternsCsv = string.Join(',', new[]
                {
                    CandlePattern.BullishEngulfing, CandlePattern.BearishEngulfing,
                    CandlePattern.Hammer, CandlePattern.ShootingStar,
                    CandlePattern.MorningStar, CandlePattern.EveningStar,
                }.Select(CandlePatternCatalog.Key)),
                IsEnabled = true,
                Notify = true,
                UpdatedBy = "seed",
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
            },
            new CandlePatternRule
            {
                Name = "CRUDEOIL nearest future — 15m",
                GroupsCsv = PatternSymbolGroups.FuturePrefix + "CRUDEOIL",
                TimeframesCsv = "15",
                PatternsCsv = all,
                IsEnabled = true,
                Notify = true,
                UpdatedBy = "seed",
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
            },
        ];
    }
}
