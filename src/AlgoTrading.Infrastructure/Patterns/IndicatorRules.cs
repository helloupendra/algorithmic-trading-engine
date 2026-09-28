using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AlgoTrading.Infrastructure.Patterns;

public enum IndicatorRuleKind
{
    RsiAbove,
    RsiBelow,
    EmaCross,
    SupertrendFlip,
    VwapCross,
}

/// <summary>
/// One indicator rule with its numbers: <c>rsi-above(14,70)</c>,
/// <c>ema-cross(9,21)</c>. Two rules are the same rule when their
/// <see cref="Key"/>s are equal; the key is what the cooldown and the
/// de-duplication are counted by.
/// </summary>
/// <param name="Period">RSI, the fast EMA, or Supertrend's ATR period.</param>
/// <param name="SlowPeriod">The slow EMA (ema-cross only).</param>
/// <param name="Level">The RSI level crossed (rsi-above / rsi-below only).</param>
/// <param name="Multiplier">ATRs from the median price to Supertrend's bands (supertrend-flip only).</param>
public sealed record IndicatorRule(IndicatorRuleKind Kind, int Period = 0, int SlowPeriod = 0, decimal Level = 0, decimal Multiplier = 0)
{
    public static readonly IndicatorRule RsiAboveDefault = new(IndicatorRuleKind.RsiAbove, 14, Level: 70);
    public static readonly IndicatorRule RsiBelowDefault = new(IndicatorRuleKind.RsiBelow, 14, Level: 30);
    public static readonly IndicatorRule EmaCrossDefault = new(IndicatorRuleKind.EmaCross, 9, 21);
    public static readonly IndicatorRule SupertrendFlipDefault = new(IndicatorRuleKind.SupertrendFlip, 10, Multiplier: 3);
    public static readonly IndicatorRule VwapCrossDefault = new(IndicatorRuleKind.VwapCross);

    /// <summary>Every rule with its default numbers, in the order the page and the messages list them.</summary>
    public static readonly IReadOnlyList<IndicatorRule> Defaults =
        [RsiAboveDefault, RsiBelowDefault, EmaCrossDefault, SupertrendFlipDefault, VwapCrossDefault];

    /// <summary>"rsi-above", "ema-cross", … — the word the config file uses.</summary>
    public string Name => NameOf(Kind);

    public static string NameOf(IndicatorRuleKind kind) => kind switch
    {
        IndicatorRuleKind.RsiAbove => "rsi-above",
        IndicatorRuleKind.RsiBelow => "rsi-below",
        IndicatorRuleKind.EmaCross => "ema-cross",
        IndicatorRuleKind.SupertrendFlip => "supertrend-flip",
        IndicatorRuleKind.VwapCross => "vwap-cross",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>"rsi-above(14,70)", "vwap-cross": canonical, as the config would spell it with every number given.</summary>
    public string Key => Kind switch
    {
        IndicatorRuleKind.RsiAbove or IndicatorRuleKind.RsiBelow => $"{Name}({Period},{Number(Level)})",
        IndicatorRuleKind.EmaCross => $"{Name}({Period},{SlowPeriod})",
        IndicatorRuleKind.SupertrendFlip => $"{Name}({Period},{Number(Multiplier)})",
        _ => Name,
    };

    /// <summary>"RSI(14) crosses above 70": what the rule watches for, in words.</summary>
    public string Label => Kind switch
    {
        IndicatorRuleKind.RsiAbove => $"RSI({Period}) crosses above {Number(Level)}",
        IndicatorRuleKind.RsiBelow => $"RSI({Period}) crosses below {Number(Level)}",
        IndicatorRuleKind.EmaCross => $"EMA({Period}) crosses EMA({SlowPeriod})",
        IndicatorRuleKind.SupertrendFlip => $"Supertrend({Period}, {Number(Multiplier)}) flips",
        _ => "Close crosses VWAP",
    };

    /// <summary>The definition the page shows beside the rule.</summary>
    public string Definition => Kind switch
    {
        IndicatorRuleKind.RsiAbove =>
            $"Wilder's RSI({Period}) on candle closes is above {Number(Level)} at this candle's close and was at or below it at the previous one.",
        IndicatorRuleKind.RsiBelow =>
            $"Wilder's RSI({Period}) on candle closes is below {Number(Level)} at this candle's close and was at or above it at the previous one.",
        IndicatorRuleKind.EmaCross =>
            $"EMA({Period}) of closes moves from below EMA({SlowPeriod}) to above it, or from above to below. Each EMA is seeded with the average of its first {Period} / {SlowPeriod} closes.",
        IndicatorRuleKind.SupertrendFlip =>
            $"Supertrend on Wilder's ATR({Period}) with bands {Number(Multiplier)} ATRs from the median price changes direction: a close through the active band turns it.",
        _ => "The close moves from one side of the session VWAP to the other. VWAP is the typical price (high + low + close) / 3 of every 1-minute bar since the open, weighted by its volume. Needs traded volume: futures and stocks, never an index.",
    };

    /// <summary>
    /// Candles the rule needs behind a candle before it may alert on it: enough
    /// for the indicator to have forgotten where the data began, so an alert
    /// does not depend on how much history happened to be stored. About five
    /// Wilder periods (the seed then weighs under 1%) for RSI and Supertrend's
    /// ATR, four slow periods for the EMAs. VWAP starts afresh every session
    /// and needs only a candle before this one.
    /// </summary>
    public int SettleCandles => Kind switch
    {
        IndicatorRuleKind.RsiAbove or IndicatorRuleKind.RsiBelow => 5 * Period,
        IndicatorRuleKind.EmaCross => 4 * SlowPeriod,
        IndicatorRuleKind.SupertrendFlip => 5 * Period,
        _ => 2,
    };

    /// <summary>VWAP is a weighting by traded volume; only an instrument that trades has one.</summary>
    public bool NeedsVolume => Kind == IndicatorRuleKind.VwapCross;

    /// <summary>The syntax each rule accepts, for the config file's header and the page.</summary>
    public static string Syntax(IndicatorRuleKind kind) => kind switch
    {
        IndicatorRuleKind.RsiAbove => "rsi-above(PERIOD,LEVEL)",
        IndicatorRuleKind.RsiBelow => "rsi-below(PERIOD,LEVEL)",
        IndicatorRuleKind.EmaCross => "ema-cross(FAST,SLOW)",
        IndicatorRuleKind.SupertrendFlip => "supertrend-flip(PERIOD,MULTIPLIER)",
        _ => "vwap-cross",
    };

    private static readonly Regex Token = new(@"^(?<name>[a-z-]+)(?:\((?<args>[^()]*)\))?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// "rsi-above(14,70)", "rsi-above" (defaults), "EMA-CROSS(5, 13)". Returns
    /// null and says why when the token is not a rule this scanner knows.
    /// </summary>
    public static IndicatorRule? TryParse(string token, out string? error)
    {
        error = null;
        var m = Token.Match(token.Trim().ToLowerInvariant().Replace(" ", string.Empty));
        if (!m.Success)
        {
            error = $"'{token}' is not a rule (for example rsi-above(14,70))";
            return null;
        }

        var name = m.Groups["name"].Value;
        var defaults = Defaults.FirstOrDefault(d => d.Name == name);
        if (defaults is null)
        {
            error = $"unknown rule '{name}'; the rules are {string.Join(", ", Defaults.Select(d => d.Name))}";
            return null;
        }

        var args = m.Groups["args"].Success && m.Groups["args"].Value.Length > 0
            ? m.Groups["args"].Value.Split(',')
            : [];
        var numbers = new List<decimal>();
        foreach (var a in args)
        {
            if (!decimal.TryParse(a, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n))
            {
                error = $"'{token}': '{a}' is not a number";
                return null;
            }

            numbers.Add(n);
        }

        int max = defaults.Kind == IndicatorRuleKind.VwapCross ? 0 : 2;
        if (numbers.Count > max)
        {
            error = max == 0
                ? $"'{token}': vwap-cross takes no numbers"
                : $"'{token}': at most two numbers, {Syntax(defaults.Kind)}";
            return null;
        }

        decimal first = numbers.Count > 0 ? numbers[0] : defaults.Period;
        decimal? second = numbers.Count > 1 ? numbers[1] : null;

        switch (defaults.Kind)
        {
            case IndicatorRuleKind.RsiAbove:
            case IndicatorRuleKind.RsiBelow:
            {
                error = WholeNumber(token, first, 2, 200, "the RSI period", out var period);
                var level = second ?? defaults.Level;
                if (error is null && (level <= 0 || level >= 100)) error = $"'{token}': the RSI level must be between 0 and 100";
                return error is null ? defaults with { Period = period, Level = level } : null;
            }
            case IndicatorRuleKind.EmaCross:
            {
                var fastError = WholeNumber(token, first, 1, 500, "the fast EMA period", out var fast);
                var slowError = WholeNumber(token, second ?? defaults.SlowPeriod, 2, 500, "the slow EMA period", out var slow);
                error = fastError ?? slowError;
                if (error is null && fast >= slow) error = $"'{token}': the fast EMA period must be below the slow one";
                return error is null ? defaults with { Period = fast, SlowPeriod = slow } : null;
            }
            case IndicatorRuleKind.SupertrendFlip:
            {
                error = WholeNumber(token, first, 1, 200, "the ATR period", out var period);
                var multiplier = second ?? defaults.Multiplier;
                if (error is null && (multiplier <= 0 || multiplier > 20)) error = $"'{token}': the multiplier must be above 0 and at most 20";
                return error is null ? defaults with { Period = period, Multiplier = multiplier } : null;
            }
            default:
                return defaults;
        }
    }

    /// <summary>Null when <paramref name="value"/> is a whole number in range; otherwise why not.</summary>
    private static string? WholeNumber(string token, decimal value, int min, int max, string what, out int result)
    {
        result = 0;
        if (value != decimal.Truncate(value) || value < min || value > max)
            return $"'{token}': {what} must be a whole number from {min} to {max}";
        result = (int)value;
        return null;
    }

    /// <summary>70 → "70", 2.5 → "2.5": numbers as a person writes them.</summary>
    public static string Number(decimal value) => value.ToString("0.##########", CultureInfo.InvariantCulture);
}

/// <summary>One watch line of the config: these symbols, on these timeframes, for these rules.</summary>
/// <param name="Number">1-based line number in the file.</param>
/// <param name="Text">The line as written, comment removed.</param>
/// <param name="Symbols">Canonical EXCHANGE:NAME symbols.</param>
/// <param name="Groups">Pattern-alert symbol groups (<c>future:NIFTY</c>, <c>indices</c>, …), resolved every scan.</param>
/// <param name="PageOnly">Record these alerts on the page and never send them.</param>
public sealed record IndicatorWatchLine(
    int Number,
    string Text,
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string> Groups,
    IReadOnlyList<int> Timeframes,
    IReadOnlyList<IndicatorRule> Rules,
    bool PageOnly);

/// <summary>
/// The indicator alerts' config file, <c>config/indicator-alerts.txt</c>: a few
/// settings, then one line per watch.
/// </summary>
/// <remarks>
/// <para>
/// The grammar, from the file's own header:
/// </para>
/// <code>
/// cooldown: 30        minutes (0 = off)
/// telegram: on        on | off
/// warmup: 250         candles of earlier sessions to read
/// SYMBOLS  TIMEFRAMES  RULE [RULE ...]  [page-only]
/// </code>
/// <para>
/// Forgiving by design, like the morning plan: a file edited by hand is read
/// line by line, a line that cannot be read is skipped with a warning naming
/// its number and the reason, and one bad entry on a line (a symbol, a
/// timeframe, a rule) costs that entry, not the line. Parsing never throws;
/// the scanner runs on whatever could be read and the page lists the rest.
/// </para>
/// </remarks>
public sealed record IndicatorAlertConfig(
    TimeSpan Cooldown,
    bool Telegram,
    int WarmupCandles,
    IReadOnlyList<IndicatorWatchLine> Lines,
    IReadOnlyList<string> Warnings)
{
    public const int DefaultCooldownMinutes = 30;
    public const int DefaultWarmupCandles = 250;
    public const int MaxWarmupCandles = 2000;
    public const int MaxCooldownMinutes = 24 * 60;
    public const string PageOnlyWord = "page-only";

    public static IndicatorAlertConfig Empty { get; } = new(TimeSpan.FromMinutes(DefaultCooldownMinutes), true, DefaultWarmupCandles, [], []);

    private static readonly Regex Setting = new(@"^(?<key>[A-Za-z][A-Za-z-]*)\s*:\s*(?<value>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Minutes = new(@"^(?<n>\d{1,5})\s*(m|min|mins|minutes?)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // A symbol line also starts with WORD: (NSE:NIFTY50-INDEX), and so does a
    // future group (future:NIFTY). Anything else of that shape is a setting.
    private static readonly string[] NotSettings = ["NSE", "BSE", "MCX", "FUTURE"];
    private static readonly string[] KnownSettings = ["cooldown", "telegram", "warmup"];

    public static IndicatorAlertConfig Parse(string? text)
    {
        var warnings = new List<string>();
        var lines = new List<IndicatorWatchLine>();
        int cooldown = DefaultCooldownMinutes, warmup = DefaultWarmupCandles;
        bool telegram = true;
        var seenSettings = new HashSet<string>(StringComparer.Ordinal);

        var raw = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < raw.Length; i++)
        {
            int number = i + 1;
            int hash = raw[i].IndexOf('#');
            string line = (hash >= 0 ? raw[i][..hash] : raw[i]).Trim();
            if (line.Length == 0) continue;

            var setting = Setting.Match(line);
            if (setting.Success
                && !NotSettings.Contains(setting.Groups["key"].Value.ToUpperInvariant())
                // "NFO:NIFTY 5 rsi-above" is a watch line with a wrong exchange,
                // and is better told so than called an unknown setting.
                && (KnownSettings.Contains(setting.Groups["key"].Value.ToLowerInvariant()) || Tokens(line).Count < 3))
            {
                var key = setting.Groups["key"].Value.ToLowerInvariant();
                var value = setting.Groups["value"].Value.Trim();
                if (!seenSettings.Add(key))
                {
                    warnings.Add($"Line {number}: a second '{key}:' line; the first one is used.");
                    continue;
                }

                switch (key)
                {
                    case "cooldown":
                    {
                        var m = Minutes.Match(value);
                        if (m.Success && int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= MaxCooldownMinutes)
                            cooldown = n;
                        else
                            warnings.Add($"Line {number}: cooldown '{value}' is not a number of minutes from 0 to {MaxCooldownMinutes}; {DefaultCooldownMinutes} is used.");
                        break;
                    }
                    case "telegram":
                    {
                        var v = value.ToLowerInvariant();
                        if (v is "on" or "yes" or "true") telegram = true;
                        else if (v is "off" or "no" or "false") telegram = false;
                        else warnings.Add($"Line {number}: telegram '{value}' is neither on nor off; on is used.");
                        break;
                    }
                    case "warmup":
                    {
                        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n <= MaxWarmupCandles)
                            warmup = n;
                        else
                            warnings.Add($"Line {number}: warmup '{value}' is not a number of candles from 0 to {MaxWarmupCandles}; {DefaultWarmupCandles} is used.");
                        break;
                    }
                    default:
                        seenSettings.Remove(key);
                        warnings.Add($"Line {number}: unknown setting '{key}:'; the settings are cooldown, telegram and warmup.");
                        break;
                }

                continue;
            }

            var parsed = ParseWatch(number, line, warnings);
            if (parsed is not null) lines.Add(parsed);
        }

        if (lines.Count == 0)
            warnings.Add("No watch line: nothing is watched. A line is SYMBOLS TIMEFRAMES RULE [RULE ...].");

        int longest = lines.SelectMany(l => l.Rules).Select(r => r.SettleCandles).DefaultIfEmpty(0).Max();
        if (warmup < longest)
        {
            var rule = lines.SelectMany(l => l.Rules).First(r => r.SettleCandles == longest);
            warnings.Add($"warmup {warmup} is below the {longest} candles {rule.Key} settles over; it alerts only once today's candles make up the rest.");
        }

        return new IndicatorAlertConfig(TimeSpan.FromMinutes(cooldown), telegram, warmup, lines, warnings);
    }

    private static IndicatorWatchLine? ParseWatch(int number, string line, List<string> warnings)
    {
        var tokens = Tokens(line);
        if (tokens.Count < 3)
        {
            warnings.Add($"Line {number}: '{line}' needs symbols, timeframes and at least one rule; skipped.");
            return null;
        }

        var symbols = new List<string>();
        var groups = new List<string>();
        foreach (var entry in tokens[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (CandlePatternRules.NormalizeSymbol(entry) is { } symbol)
            {
                if (!symbols.Contains(symbol)) symbols.Add(symbol);
            }
            else if (PatternSymbolGroups.Normalize(entry) is { } group)
            {
                if (!groups.Contains(group)) groups.Add(group);
            }
            else
            {
                warnings.Add($"Line {number}: '{entry}' is neither EXCHANGE:NAME on NSE, BSE or MCX nor future:UNDERLYING; left out.");
            }
        }

        var timeframes = new List<int>();
        foreach (var entry in tokens[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Minutes.Match(entry);
            if (m.Success && int.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var tf)
                && SessionBarAggregator.SupportedTimeframes.Contains(tf))
            {
                if (!timeframes.Contains(tf)) timeframes.Add(tf);
            }
            else
            {
                warnings.Add($"Line {number}: timeframe '{entry}' is not one of {string.Join(", ", SessionBarAggregator.SupportedTimeframes)} minutes; left out.");
            }
        }

        var rules = new List<IndicatorRule>();
        bool pageOnly = false;
        foreach (var token in tokens.Skip(2))
        {
            if (string.Equals(token, PageOnlyWord, StringComparison.OrdinalIgnoreCase))
            {
                pageOnly = true;
                continue;
            }

            var rule = IndicatorRule.TryParse(token, out var error);
            if (rule is null)
            {
                warnings.Add($"Line {number}: {error}; left out.");
                continue;
            }

            if (!rules.Any(r => r.Key == rule.Key)) rules.Add(rule);
        }

        if (symbols.Count + groups.Count == 0 || timeframes.Count == 0 || rules.Count == 0)
        {
            var missing = symbols.Count + groups.Count == 0 ? "no symbol" : timeframes.Count == 0 ? "no timeframe" : "no rule";
            warnings.Add($"Line {number}: {missing} left that could be read; the line is skipped.");
            return null;
        }

        if (rules.Any(r => r.NeedsVolume))
        {
            // Said here, where it is written, and again by the scanner for any
            // index a group resolves to. Never computed: a VWAP of an index would
            // be a volume weighting with no volume, i.e. invented.
            foreach (var index in symbols.Where(IsIndexSymbol))
                warnings.Add($"Line {number}: vwap-cross on {index}: an index has no traded volume, so it has no VWAP; not watched there.");
            if (groups.Contains(PatternSymbolGroups.Indices))
                warnings.Add($"Line {number}: vwap-cross on 'indices': an index has no traded volume, so it has no VWAP; not watched there.");
        }

        return new IndicatorWatchLine(
            number,
            line,
            symbols,
            groups,
            timeframes.Order().ToList(),
            rules.OrderBy(r => r.Kind).ThenBy(r => r.Key, StringComparer.Ordinal).ToList(),
            pageOnly);
    }

    /// <summary>An index is calculated, not traded: NSE:NIFTY50-INDEX, BSE:SENSEX-INDEX.</summary>
    public static bool IsIndexSymbol(string symbol) => symbol.EndsWith("-INDEX", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whitespace-separated, except inside parentheses: "rsi-above(14, 70)" is one token.</summary>
    private static List<string> Tokens(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        int depth = 0;
        foreach (var ch in line)
        {
            if (ch == '(') depth++;
            else if (ch == ')') depth = Math.Max(0, depth - 1);

            if (char.IsWhiteSpace(ch) && depth == 0)
            {
                if (current.Length > 0) tokens.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
