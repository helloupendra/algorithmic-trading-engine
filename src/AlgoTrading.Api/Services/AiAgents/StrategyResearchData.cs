using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.AiAgents;

/// <summary>The live paper runs of a period, for the Strategy Researcher: recaps and alert runs left out, net after charges.</summary>
public interface IPaperResults
{
    Task<IReadOnlyList<LiveRunSummaryResponse>> RunsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);
}

/// <summary>The paper runs as Trade → History reads them (<see cref="LiveRunHistoryBuilder"/>), a page at a time.</summary>
public sealed class LivePaperResults(LiveRunHistoryBuilder history) : IPaperResults
{
    private const int MaxRuns = 3000;

    public async Task<IReadOnlyList<LiveRunSummaryResponse>> RunsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var runs = new List<LiveRunSummaryResponse>();
        for (int skip = 0; skip < MaxRuns; skip += LiveRunHistoryFilter.MaxTake)
        {
            var page = await history.ListAsync(new LiveRunHistoryFilter(null, null, null, null, from, to, LiveRunHistoryFilter.MaxTake, skip), cancellationToken);
            runs.AddRange(page);
            if (page.Count < LiveRunHistoryFilter.MaxTake) break;
        }

        return runs.Where(r => r.Role != "alerts" && !r.IsRecap).ToList();
    }
}

/// <summary>One finished backtest of the week, as the researcher reads it.</summary>
/// <param name="Periods">Which of holdout (2020-09 to 2023-12), design (2024–2025) and recent (2026 on) its window covers.</param>
/// <param name="Tunables">Settings a person chose: the strategy's own parameters and every rule set on the run.</param>
/// <param name="Config">A short hash of those settings: runs with the same one are the same configuration.</param>
public sealed record BacktestRow(
    long RunId, string Strategy, string Underlying, string Resolution, string From, string To, IReadOnlyList<string> Periods,
    int Trades, decimal Net, decimal PerTrade, decimal WinRate, int Tunables, string Config, string Status);

/// <summary>A strategy's live paper runs over a period, after charges.</summary>
public sealed record PaperRow(string Strategy, string Period, int Runs, int Days, int Trades, decimal Net, decimal Charges, decimal PerTrade);

/// <summary>One overfitting check code ran on a strategy: its id, the code's reading (concern, fine, unknown) and the facts with their numbers.</summary>
public sealed record StrategyCheck(string Strategy, string Check, string Reading, string Facts);

/// <summary>A strategy's written spec, cut to what the researcher needs: the idea and the limitations.</summary>
public sealed record SpecExcerpt(string Strategy, string? Idea, string? Limitations);

/// <summary>Everything the researcher is handed for a week.</summary>
public sealed record ResearchWeek(
    DateOnly Week, DateTime FromUtc, DateTime ToUtc, IReadOnlyList<BacktestRow> Backtests, IReadOnlyList<PaperRow> Paper,
    IReadOnlyList<StrategyCheck> Checks, IReadOnlyList<SpecExcerpt> Specs)
{
    public IReadOnlyList<string> Strategies => Checks.Select(c => c.Strategy).Distinct().ToList();
}

/// <summary>
/// The Strategy Researcher's tables: the week's finished backtests, each strategy's paper results after charges,
/// its spec, and the overfitting checks code runs on them. Code computes every number; the model only words them.
/// </summary>
/// <remarks>
/// <para>
/// The checks, per strategy (<see cref="Checks"/>):
/// </para>
/// <list type="bullet">
/// <item><c>sample</c>: closed trades in the week's backtests; under <see cref="MinTrades"/> is a coin, not an edge.</item>
/// <item><c>tunables</c>: trades per tunable setting; under <see cref="MinTradesPerTunable"/> the settings can fit the noise.</item>
/// <item><c>holdout</c>: whether any of its backtests ran on 2020-09 to 2023-12, the period nothing was tuned on. The
/// desk's rule (docs/strategies/SmcStructureBreak.md): a 2024–26 result is not believed until it has.</item>
/// <item><c>in-vs-out</c>: per-trade net on the design years (2024–2025) against a later or held-out period.</item>
/// <item><c>paper-vs-backtest</c>: the last 30 days' paper per-trade net after charges against the backtests'.</item>
/// <item><c>configs</c>: configurations tried this week; the best of many is likely the luckiest.</item>
/// </list>
/// <para>
/// Bounded for the shared box: the week's backtests (at most <see cref="MaxBacktests"/>), their positions and filled
/// orders by run id, and the paper runs of 30 days through the history page's own builder.
/// </para>
/// </remarks>
public sealed partial class StrategyResearchData(TradingDbContext db, IPaperResults paper, IWebHostEnvironment environment)
{
    public const int MaxBacktests = 300;
    public const int MinTrades = 30;
    public const int MinTradesPerTunable = 10;
    public const int MinPaperTrades = 20;
    public const int ManyConfigs = 5;
    public const int PaperDays = 30;

    public static readonly DateOnly HoldoutFrom = new(2020, 9, 1);
    public static readonly DateOnly HoldoutTo = new(2023, 12, 31);
    public static readonly DateOnly DesignFrom = new(2024, 1, 1);
    public static readonly DateOnly DesignTo = new(2025, 12, 31);

    /// <summary>The desk's rules, handed to the model with the tables (and so the numbers it may cite from them).</summary>
    public const string Rules =
        "RULES: the holdout is 2020-09-01 to 2023-12-31, the years the expired-options history covers and nothing was " +
        "tuned on (2020-23). A rule found on 2024 or later is not believed until it is run, unchanged, on the holdout: " +
        "SmcStructureBreak's counter-daily breaks made money on 2024-26 and lost it on 2020-23. The design years are " +
        "2024-01-01 to 2025-12-31; 2026 on is recent. Fewer than 30 closed trades is a coin, not an edge; fewer than 10 " +
        "trades per tunable setting can fit noise; 5 or more configurations tried in a week means the best is likely the " +
        "luckiest. Paper is judged after charges; fewer than 20 paper trades tells little.";

    /// <summary>Where the strategies' specs are read from: docs/strategies at the repo root unless a test says otherwise.</summary>
    public string? SpecsFolder { get; set; }

    private static readonly HashSet<string> RunKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "lots", "quantity", "underlying", "resolution", "eod_square_off_ist", "eodSquareOffIst", "charges_per_lot", "chargesPerLot",
        "lot_size", "lotSize", "lot_size_source", "lotSizeSource", "instrument_kind", "costs", "role", "recap", "account",
    };

    private static readonly string[] RuleBlocks = ["risk", "filters", "limits", "exits", "contract"];

    public async Task<ResearchWeek> BuildAsync(DateOnly week, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        var backtests = await BacktestsAsync(fromUtc, toUtc, cancellationToken);
        var today = IstTime.DateOf(toUtc);
        var runs = await paper.RunsAsync(today.AddDays(-(PaperDays - 1)), today, cancellationToken);
        var weekFrom = IstTime.DateOf(fromUtc);
        var paperRows = PaperRows(runs, $"last {PaperDays} days", _ => true)
            .Concat(PaperRows(runs, "last 7 days", r => IstTime.DateOf(r.StartedUtc) >= weekFrom))
            .ToList();
        // Strategies the week touched: a backtest this week, or a paper run in its last 7 days.
        var names = backtests.Select(b => b.Strategy)
            .Concat(paperRows.Where(p => p.Period == "last 7 days").Select(p => p.Strategy))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        var checks = names.SelectMany(n => Checks(n, backtests.Where(b => b.Strategy == n).ToList(),
            paperRows.FirstOrDefault(p => p.Strategy == n && p.Period == $"last {PaperDays} days"))).ToList();
        var specs = new List<SpecExcerpt>();
        foreach (var n in names) specs.Add(await SpecAsync(n, cancellationToken));
        return new ResearchWeek(week, fromUtc, toUtc, backtests, paperRows.Where(p => names.Contains(p.Strategy)).ToList(), checks, specs);
    }

    /// <summary>The week's finished backtests (completed, or stopped with their trades), newest first, with their net as the Backtests list shows it.</summary>
    public async Task<List<BacktestRow>> BacktestsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        string[] finished = [BacktestRunControl.RunStatusCompleted, BacktestRunControl.RunStatusStopped];
        var runs = await db.SimulationRuns.AsNoTracking()
            .Where(r => r.Mode == BacktestRunViewBuilder.OfflineReplayMode && finished.Contains(r.Status) && r.CreatedUtc >= fromUtc && r.CreatedUtc < toUtc)
            .OrderByDescending(r => r.CreatedUtc).ThenByDescending(r => r.Id)
            .Take(MaxBacktests)
            .Select(r => new { r.Id, r.StrategyName, r.Symbol, r.Resolution, r.FromUtc, r.ToUtc, r.ParametersJson, r.Status })
            .ToListAsync(cancellationToken);
        if (runs.Count == 0) return [];

        var ids = runs.Select(r => r.Id).ToList();
        var stats = (await db.PaperPositions.AsNoTracking()
                .Where(p => ids.Contains(p.SimulationRunId))
                .GroupBy(p => p.SimulationRunId)
                .Select(g => new
                {
                    RunId = g.Key,
                    Realized = g.Sum(x => x.RealizedPnl),
                    Closed = g.Count(x => x.Status == "Closed"),
                    Wins = g.Count(x => x.Status == "Closed" && x.RealizedPnl > 0),
                })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.RunId);
        var lots = await db.PaperOrders.AsNoTracking()
            .Where(o => ids.Contains(o.SimulationRunId) && o.Status == "Filled")
            .GroupBy(o => o.SimulationRunId)
            .Select(g => new { RunId = g.Key, Lots = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.RunId, x => x.Lots, cancellationToken);

        var rows = new List<BacktestRow>();
        foreach (var r in runs)
        {
            var p = BacktestRunParameters.Parse(r.ParametersJson);
            stats.TryGetValue(r.Id, out var s);
            decimal charges = p.ChargesPerLot is > 0 && lots.TryGetValue(r.Id, out var filled) ? p.ChargesPerLot.Value * filled : 0m;
            decimal net = Math.Round((s?.Realized ?? 0m) - charges, 2);
            int trades = s?.Closed ?? 0;
            var from = r.FromUtc is DateTime f ? IstTime.DateOf(f) : (DateOnly?)null;
            var to = r.ToUtc is DateTime t ? IstTime.DateOf(t) : (DateOnly?)null;
            var (tunables, config) = Settings(r.ParametersJson);
            rows.Add(new BacktestRow(r.Id, r.StrategyName,
                p.Underlying ?? UnderlyingCatalog.UnderlyingForSpot(r.Symbol) ?? r.Symbol,
                ResolutionCodes.ToCandle(r.Resolution),
                from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                to?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                from is DateOnly a && to is DateOnly b ? Periods(a, b) : [],
                trades, net, trades == 0 ? 0m : Math.Round(net / trades, 2),
                trades == 0 ? 0m : Math.Round(100m * (s?.Wins ?? 0) / trades, 1),
                tunables, config, r.Status));
        }

        return rows;
    }

    /// <summary>Which of holdout, design and recent a window covers.</summary>
    public static IReadOnlyList<string> Periods(DateOnly from, DateOnly to)
    {
        var list = new List<string>();
        if (from <= HoldoutTo && to >= HoldoutFrom) list.Add("holdout");
        if (from <= DesignTo && to >= DesignFrom) list.Add("design");
        if (to > DesignTo) list.Add("recent");
        return list;
    }

    /// <summary>
    /// The settings a person chose for a run: the strategy's own parameters and each rule set in its risk, filters,
    /// limits, exits and contract blocks (the run's lots, underlying and cost model are not tunables), and a short hash
    /// of them that is the same for the same configuration.
    /// </summary>
    public static (int Tunables, string Config) Settings(string? parametersJson)
    {
        JsonObject root;
        try
        {
            root = (string.IsNullOrWhiteSpace(parametersJson) ? null : JsonNode.Parse(parametersJson) as JsonObject) ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            root = new JsonObject();
        }

        var leaves = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in root)
        {
            if (RunKeys.Contains(key) || value is null) continue;
            if (RuleBlocks.Contains(key, StringComparer.OrdinalIgnoreCase)) Leaves(key, value, leaves);
            else leaves[key] = value.ToJsonString();
        }

        string canonical = string.Join(";", leaves.Select(kv => kv.Key + "=" + kv.Value));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..8].ToLowerInvariant();
        return (leaves.Count, hash);
    }

    private static void Leaves(string path, JsonNode node, SortedDictionary<string, string> into)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o) if (v is not null) Leaves($"{path}.{k}", v, into);
                break;
            case JsonArray a:
                if (a.Count > 0) into[path] = a.ToJsonString();
                break;
            case JsonValue v:
                // A rule switched off (false, 0, empty) is not a choice the run made.
                if (v.TryGetValue(out bool b) && !b) break;
                if (v.TryGetValue(out string? s) && string.IsNullOrWhiteSpace(s)) break;
                into[path] = v.ToJsonString();
                break;
        }
    }

    private static IEnumerable<PaperRow> PaperRows(IReadOnlyList<LiveRunSummaryResponse> runs, string period, Func<LiveRunSummaryResponse, bool> keep) =>
        runs.Where(keep).GroupBy(r => r.StrategyName, StringComparer.Ordinal).Select(g =>
        {
            int trades = g.Sum(r => r.Trades);
            decimal net = Math.Round(g.Sum(r => r.NetPnl), 2);
            return new PaperRow(g.Key, period, g.Count(), g.Select(r => IstTime.DateOf(r.StartedUtc)).Distinct().Count(), trades, net,
                Math.Round(g.Sum(r => r.Charges), 2), trades == 0 ? 0m : Math.Round(net / trades, 2));
        });

    /// <summary>The overfitting checks of one strategy, from its week's backtests and its paper results.</summary>
    public static List<StrategyCheck> Checks(string strategy, IReadOnlyList<BacktestRow> backtests, PaperRow? paper30)
    {
        var checks = new List<StrategyCheck>();
        int trades = backtests.Sum(b => b.Trades);
        decimal net = backtests.Sum(b => b.Net);
        decimal perTrade = trades == 0 ? 0m : Math.Round(net / trades, 2);
        string n = backtests.Count.ToString(CultureInfo.InvariantCulture);

        // sample
        checks.Add(backtests.Count == 0
            ? new StrategyCheck(strategy, "sample", "unknown", "No backtest of it finished this week.")
            : new StrategyCheck(strategy, "sample", trades < MinTrades ? "concern" : "fine",
                $"{trades} closed trades in {n} backtests this week, net {Rs(net)}, {Rs(perTrade)} a trade."));

        // tunables
        if (backtests.Count > 0)
        {
            int tunables = backtests.Max(b => b.Tunables);
            decimal per = tunables == 0 ? trades : Math.Round((decimal)trades / tunables, 1);
            checks.Add(new StrategyCheck(strategy, "tunables", tunables > 0 && per < MinTradesPerTunable ? "concern" : "fine",
                $"Up to {tunables} tunable settings against {trades} trades: {per.ToString("0.#", CultureInfo.InvariantCulture)} trades per setting."));
        }

        // holdout
        var holdout = backtests.Where(b => b.Periods.Contains("holdout")).ToList();
        if (backtests.Count == 0)
        {
            checks.Add(new StrategyCheck(strategy, "holdout", "unknown", "No backtest this week, so nothing to check on the holdout."));
        }
        else if (holdout.Count == 0)
        {
            checks.Add(new StrategyCheck(strategy, "holdout", backtests.Any(b => b.Periods.Contains("design") || b.Periods.Contains("recent")) ? "concern" : "unknown",
                $"None of its {n} backtests this week ran on the 2020-09-01 to 2023-12-31 holdout."));
        }
        else
        {
            int ht = holdout.Sum(b => b.Trades);
            decimal hn = holdout.Sum(b => b.Net);
            checks.Add(new StrategyCheck(strategy, "holdout", hn > 0 ? "fine" : "concern",
                $"{holdout.Count} backtests touched the holdout: {ht} trades, net {Rs(hn)}, {Rs(ht == 0 ? 0m : Math.Round(hn / ht, 2))} a trade."));
        }

        // in vs out of sample: windows inside one period only
        var design = backtests.Where(b => b.Periods.Count == 1 && b.Periods[0] == "design").ToList();
        var outside = backtests.Where(b => b.Periods.Count == 1 && b.Periods[0] is "recent" or "holdout").ToList();
        if (design.Count > 0 && outside.Count > 0)
        {
            var (dt, dn) = (design.Sum(b => b.Trades), design.Sum(b => b.Net));
            var (ot, on) = (outside.Sum(b => b.Trades), outside.Sum(b => b.Net));
            decimal dp = dt == 0 ? 0m : Math.Round(dn / dt, 2);
            decimal op = ot == 0 ? 0m : Math.Round(on / ot, 2);
            bool decayed = dp > 0 && (op <= 0 || op < dp / 2);
            checks.Add(new StrategyCheck(strategy, "in-vs-out", decayed ? "concern" : "fine",
                $"Design years (2024-2025): {dt} trades, {Rs(dp)} a trade; outside them: {ot} trades, {Rs(op)} a trade."));
        }
        else
        {
            checks.Add(new StrategyCheck(strategy, "in-vs-out", "unknown",
                "This week's backtests do not give both a design-years window and one outside it to compare."));
        }

        // paper vs backtest
        if (paper30 is null || paper30.Trades == 0)
        {
            checks.Add(new StrategyCheck(strategy, "paper-vs-backtest", "unknown", $"No closed paper trades in the last {PaperDays} days."));
        }
        else if (backtests.Count == 0 || trades == 0)
        {
            checks.Add(new StrategyCheck(strategy, "paper-vs-backtest", "unknown",
                $"Paper, last {PaperDays} days: {paper30.Trades} trades, net {Rs(paper30.Net)} after {Rs(paper30.Charges)} charges; no backtest this week to compare."));
        }
        else
        {
            bool thin = paper30.Trades < MinPaperTrades;
            bool worse = perTrade > 0 && (paper30.PerTrade <= 0 || paper30.PerTrade < perTrade / 2);
            checks.Add(new StrategyCheck(strategy, "paper-vs-backtest", thin ? "unknown" : worse ? "concern" : "fine",
                $"Paper, last {PaperDays} days: {paper30.Trades} trades, {Rs(paper30.PerTrade)} a trade after charges; this week's backtests: {Rs(perTrade)} a trade."));
        }

        // configurations tried
        if (backtests.Count > 0)
        {
            int configs = backtests.Select(b => b.Config).Distinct().Count();
            checks.Add(new StrategyCheck(strategy, "configs", configs >= ManyConfigs ? "concern" : "fine",
                $"{configs} configurations tried in {n} backtests this week."));
        }

        return checks;
    }

    /// <summary>The spec's Idea and Limitations, at most 600 characters each; null parts when the spec or the section is missing.</summary>
    public async Task<SpecExcerpt> SpecAsync(string strategy, CancellationToken cancellationToken)
    {
        if (!SpecName().IsMatch(strategy)) return new SpecExcerpt(strategy, null, null);
        string folder = Path.GetFullPath(SpecsFolder ?? Path.Combine(environment.ContentRootPath, "..", "..", "docs", "strategies"));
        string file = Path.GetFullPath(Path.Combine(folder, strategy + ".md"));
        if (!file.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file)) return new SpecExcerpt(strategy, null, null);
        string markdown = Comments().Replace(await File.ReadAllTextAsync(file, cancellationToken), string.Empty);
        return new SpecExcerpt(strategy, Section(markdown, "Idea"), Section(markdown, "Limitations"));
    }

    private static string? Section(string markdown, string heading)
    {
        var m = Regex.Match(markdown, $@"^##\s+{Regex.Escape(heading)}\s*$(?<body>.*?)(?=^##\s|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
        if (!m.Success) return null;
        string text = Regex.Replace(m.Groups["body"].Value, @"\s+", " ").Trim();
        return text.Length == 0 ? null : text.Length <= 600 ? text : text[..599] + "…";
    }

    private static string Rs(decimal value) =>
        (value < 0 ? "-₹" : "₹") + Math.Abs(value).ToString("#,##0.##", CultureInfo.InvariantCulture);

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex SpecName();

    [GeneratedRegex(@"<!--[\s\S]*?-->")]
    private static partial Regex Comments();
}
