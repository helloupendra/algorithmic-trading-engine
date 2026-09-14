using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Patterns;

/// <summary>What is watched on one symbol at one timeframe, merged across every rule that covers it.</summary>
public sealed class PatternWatch
{
    public required string Symbol { get; init; }
    public required int TimeframeMinutes { get; init; }

    /// <summary>Patterns any enabled rule asks for here.</summary>
    public HashSet<CandlePattern> Patterns { get; } = [];

    /// <summary>Patterns at least one of those rules sends to Telegram.</summary>
    public HashSet<CandlePattern> NotifyPatterns { get; } = [];

    public SortedSet<string> RuleNames { get; } = new(StringComparer.Ordinal);
}

/// <summary>Every (symbol, timeframe) to scan, and the rule entries that resolved to nothing.</summary>
public sealed record PatternWatchPlan(
    IReadOnlyList<PatternWatch> Watches,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SymbolsByGroup,
    IReadOnlyList<string> Unresolved)
{
    public IReadOnlyList<string> Symbols => Watches.Select(w => w.Symbol).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
}

/// <summary>
/// Turns rules into concrete symbols: explicit ones as written, groups resolved
/// against the recording list and the instrument master at the moment of asking.
/// </summary>
/// <remarks>
/// Futures are resolved on every call, never stored: CRUDEOIL26SEPFUT stops
/// trading on 21 Sep, and a rule that named it would go silent the next morning
/// looking exactly like a quiet market.
/// </remarks>
public sealed class PatternWatchPlanner
{
    private readonly TradingDbContext _db;

    public PatternWatchPlanner(TradingDbContext db)
    {
        _db = db;
    }

    public async Task<PatternWatchPlan> PlanAsync(
        IEnumerable<ParsedPatternRule> rules,
        DateOnly istToday,
        CancellationToken cancellationToken = default)
    {
        var enabled = rules.Where(r => r.IsEnabled && r.Timeframes.Count > 0 && r.Patterns.Count > 0).ToList();
        var groups = enabled.SelectMany(r => r.Groups).Distinct(StringComparer.Ordinal).ToList();
        var symbolsByGroup = await ResolveGroupsAsync(groups, istToday, cancellationToken);

        var unresolved = new List<string>();
        var watches = new Dictionary<(string, int), PatternWatch>();

        foreach (var rule in enabled)
        {
            var symbols = new List<string>(rule.Symbols);
            foreach (var group in rule.Groups)
            {
                var members = symbolsByGroup.GetValueOrDefault(group) ?? [];
                if (members.Count == 0) unresolved.Add($"{rule.Name}: \"{group}\" resolved to no symbols.");
                symbols.AddRange(members);
            }

            foreach (var symbol in symbols.Distinct(StringComparer.Ordinal))
            {
                foreach (var tf in rule.Timeframes)
                {
                    if (!watches.TryGetValue((symbol, tf), out var watch))
                    {
                        watch = new PatternWatch { Symbol = symbol, TimeframeMinutes = tf };
                        watches[(symbol, tf)] = watch;
                    }

                    watch.RuleNames.Add(rule.Name);
                    watch.Patterns.UnionWith(rule.Patterns);
                    if (rule.Notify) watch.NotifyPatterns.UnionWith(rule.Patterns);
                }
            }
        }

        return new PatternWatchPlan(
            watches.Values.OrderBy(w => w.Symbol, StringComparer.Ordinal).ThenBy(w => w.TimeframeMinutes).ToList(),
            symbolsByGroup,
            unresolved);
    }

    private async Task<Dictionary<string, IReadOnlyList<string>>> ResolveGroupsAsync(
        IReadOnlyList<string> groups,
        DateOnly istToday,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (groups.Count == 0) return result;

        List<string> recording = [];
        if (groups.Contains(PatternSymbolGroups.Indices) || groups.Contains(PatternSymbolGroups.RecordingStocks))
        {
            recording = await _db.LiveWatchlistItems.AsNoTracking()
                .Where(x => x.IsActive)
                .Select(x => x.Symbol)
                .ToListAsync(cancellationToken);
        }

        // Every underlying whose nearest future any group needs, in one query.
        var indexNames = UnderlyingCatalog.IndexUnderlyings;
        var commodityNames = UnderlyingCatalog.CommodityUnderlyings;
        var single = groups.Where(g => g.StartsWith(PatternSymbolGroups.FuturePrefix, StringComparison.Ordinal))
            .Select(g => g[PatternSymbolGroups.FuturePrefix.Length..])
            .ToList();
        var wanted = new HashSet<string>(single, StringComparer.Ordinal);
        if (groups.Contains(PatternSymbolGroups.IndexFutures)) wanted.UnionWith(indexNames);
        if (groups.Contains(PatternSymbolGroups.McxFutures)) wanted.UnionWith(commodityNames);

        var futures = new List<(string Symbol, string Underlying, string Exchange, DateOnly Expiry)>();
        if (wanted.Count > 0)
        {
            var names = wanted.ToList();
            var rows = await _db.Instruments.AsNoTracking()
                .Where(i => i.IsEnabled && i.InstrumentType == "FUT" && i.ExpiryDate != null
                            && i.ExpiryDate >= istToday && names.Contains(i.Underlying))
                .Select(i => new { i.Symbol, i.Underlying, i.Exchange, Expiry = i.ExpiryDate!.Value })
                .ToListAsync(cancellationToken);
            futures = rows.Select(r => (r.Symbol, r.Underlying, r.Exchange.ToUpperInvariant(), r.Expiry)).ToList();
        }

        string? Nearest(string underlying, Func<string, bool> exchange) => futures
            .Where(f => f.Underlying == underlying && exchange(f.Exchange))
            .OrderBy(f => f.Expiry).ThenBy(f => f.Symbol, StringComparer.Ordinal)
            .Select(f => f.Symbol)
            .FirstOrDefault();

        foreach (var group in groups)
        {
            IEnumerable<string> members = group switch
            {
                PatternSymbolGroups.Indices => indexNames.Select(UnderlyingCatalog.SpotSymbolFor)
                    .Concat(recording.Where(s => s.EndsWith("-INDEX", StringComparison.OrdinalIgnoreCase))),
                PatternSymbolGroups.RecordingStocks => recording.Where(s => s.EndsWith("-EQ", StringComparison.OrdinalIgnoreCase)),
                PatternSymbolGroups.IndexFutures => indexNames.Select(n => Nearest(n, ex => ex != "MCX")).OfType<string>(),
                PatternSymbolGroups.McxFutures => commodityNames.Select(n => Nearest(n, ex => ex == "MCX")).OfType<string>(),
                _ when group.StartsWith(PatternSymbolGroups.FuturePrefix, StringComparison.Ordinal)
                    => new[] { Nearest(group[PatternSymbolGroups.FuturePrefix.Length..], _ => true) }.OfType<string>(),
                _ => [],
            };

            result[group] = members
                .Select(CandlePatternRules.NormalizeSymbol)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        return result;
    }
}
