using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Patterns;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Smc;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.AiAgents;

/// <summary>A price an index's setup is read against, as code found it.</summary>
/// <param name="Id">"L1", "L2"…, highest first.</param>
/// <param name="Side">"above", "below" or "at" the last price.</param>
/// <param name="Distance">The level minus the last price, in points.</param>
public sealed record TechnicalLevel(string Id, decimal Price, string What, string Side, decimal Distance);

/// <summary>One index's technical facts, computed by code from its recorded minute bars; or why there are none.</summary>
public sealed class IndexTechnicals
{
    public required string Underlying { get; init; }

    /// <summary>Why the index cannot be graded (no bars recorded); null when it can.</summary>
    public string? NotAvailable { get; init; }

    /// <summary>What the model reads about it.</summary>
    public JsonObject Json { get; init; } = new();

    public IReadOnlyList<TechnicalLevel> Levels { get; init; } = [];

    /// <summary>Every number in <see cref="Json"/>, as written there.</summary>
    public FactNumbers Numbers { get; init; } = new();
}

/// <summary>The Technical Analyst's facts: each index's, and the indices the desk holds open options on.</summary>
public sealed class TechnicalFacts : IAnalystFacts
{
    public required DateTime AsOfUtc { get; init; }

    public required IReadOnlyList<IndexTechnicals> Indices { get; init; }

    public IReadOnlyList<string> OpenOn { get; init; } = [];

    public JsonNode Data => new JsonObject
    {
        ["asOf"] = IstTime.ToIst(AsOfUtc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        ["indices"] = new JsonArray(Indices.Select(i => i.NotAvailable is null
            ? (JsonNode)i.Json.DeepClone()
            : new JsonObject { ["underlying"] = i.Underlying, ["notAvailable"] = i.NotAvailable }).ToArray()),
        ["openPositionsOn"] = new JsonArray(OpenOn.Select(o => (JsonNode)o).ToArray()),
    };

    public string Text =>
        $"TECHNICAL FACTS, {IstTime.ToIst(AsOfUtc).ToString("ddd d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)} IST. Computed by the desk's code " +
        "from its recorded 1-minute bars (EMAs and ATR on 5-minute bars, swings on 15-minute bars). Copy numbers from here; " +
        "key levels only from each index's levels.\n" + Data.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public string? Nothing => Indices.All(i => i.NotAvailable is not null)
        ? "No index has bars to read: " + string.Join("; ", Indices.Select(i => $"{i.Underlying}: {i.NotAvailable}"))
        : null;

    /// <summary>Every number handed over, all indices together: what a reason or the summary may state.</summary>
    public FactNumbers AllNumbers => Indices.Aggregate(new FactNumbers(), (all, i) => all.With(i.Numbers));
}

/// <summary>
/// The Technical Analyst (owner, 10 Oct 2026): grades the setup of NIFTY, BANKNIFTY and SENSEX from levels and trends
/// the code computes, at 08:20 IST on trading days and on the half hour of the session while the desk holds an open
/// index option.
/// </summary>
/// <remarks>
/// <para>
/// The facts are the brief's own arithmetic (<see cref="MarketBriefBuilder.FiveMinute"/>, <see cref="MarketBriefBuilder.Atr"/>,
/// <see cref="IndicatorMath.Ema"/>, the regular session as <see cref="SituationMath.Session"/> reads it), plus levels:
/// the session's and the session before's highs and lows, the previous close, the EMAs, and swing highs and lows on
/// 15-minute bars (<see cref="MarketStructure"/>'s fractal reading, two bars each side). An index has no VWAP: it trades
/// no volume.
/// </para>
/// <para>
/// The model gives each index a bias, a grade from A to D and its key levels. Each key level must be one of the
/// index's levels and every number in a reason one the code handed over (<see cref="FactNumbers"/>); an answer that
/// fails is kept as <see cref="AiReportStatus.Invalid"/> with the reason.
/// </para>
/// </remarks>
public sealed class TechnicalAnalystAgent(
    TradingDbContext db,
    AiGateway gateway,
    AiReportWriter reports,
    ILiveDataService live,
    IMarketSessionService sessions,
    ILogger<TechnicalAnalystAgent> logger,
    TimeProvider? time = null) : SlotAnalystAgent<TechnicalFacts>(gateway, reports, sessions, logger, time)
{
    public static readonly IReadOnlySet<string> Biases = new HashSet<string>(StringComparer.Ordinal) { "bullish", "bearish", "neutral" };

    public static readonly IReadOnlySet<string> Grades = new HashSet<string>(StringComparer.Ordinal) { "A", "B", "C", "D" };

    /// <summary>08:20, written until the 09:15 open.</summary>
    public static readonly IReadOnlyList<AnalystSlot> MorningSlots = [new(new TimeOnly(8, 20), TimeSpan.FromMinutes(55))];

    /// <summary>The half hours from 09:30 to 15:00, each written within its half hour, only while an index option is open.</summary>
    public static readonly IReadOnlyList<AnalystSlot> PositionSlots =
        Enumerable.Range(0, 12).Select(i => new AnalystSlot(new TimeOnly(9, 30).AddMinutes(30 * i), TimeSpan.FromMinutes(30))).ToList();

    /// <summary>Key levels a grade names, at most.</summary>
    public const int MaxKeyLevels = 4;

    /// <summary>Swing levels kept on each side of the price.</summary>
    private const int SwingsEachSide = 3;

    public override string AgentKey => AiCatalog.TechnicalAnalyst;

    public override IReadOnlyList<AnalystSlot> Slots => [.. MorningSlots, .. PositionSlots];

    protected override string Label => "Technical";

    /// <summary>
    /// 08:20 on a trading day; and a half hour of the session when the AI Trader (its shadow book or its account) or a
    /// strategy run holds an open NIFTY, BANKNIFTY or SENSEX option then. Outside the session's hours (the calendar's,
    /// a half day included) no position slot is written.
    /// </summary>
    protected override async Task<(DateOnly Day, TimeOnly At)?> SlotNowAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (AnalystSchedule.Current(nowUtc, MorningSlots, Sessions) is { } morning) return morning;
        if (AnalystSchedule.Current(nowUtc, PositionSlots, Sessions) is not { } held) return null;
        var info = Sessions.GetSessionInfo(nowUtc, "NSE", "FO");
        if (nowUtc < info.SessionOpenUtc || nowUtc >= info.SessionCloseUtc) return null;
        return (await OpenIndexOptionsAsync(db, held.Day, cancellationToken)).Count > 0 ? held : null;
    }

    /// <summary>
    /// The indices the desk holds an open option on: the AI Trader's live shadow book of <paramref name="day"/>, and the
    /// open legs of live strategy runs (its own account's included), recaps left out.
    /// </summary>
    public static async Task<List<string>> OpenIndexOptionsAsync(TradingDbContext db, DateOnly day, CancellationToken cancellationToken)
    {
        var shadow = await db.AiTraderShadowPositions.AsNoTracking()
            .Where(p => p.ExitUtc == null && p.ReplaySessionId == null && p.Day == day)
            .Select(p => p.Underlying)
            .ToListAsync(cancellationToken);

        var runs = db.SimulationRuns.AsNoTracking()
            .Where(r => r.Mode == StrategyRunControl.LivePaperMode
                        && (r.Status == StrategyRunControl.RunStatusRunning || r.Status == StrategyRunControl.RunStatusStopping))
            .WithoutRecaps();
        var legs = await (from p in db.PaperPositions.AsNoTracking()
                          join r in runs on p.SimulationRunId equals r.Id
                          where p.Status == "Open" && p.Quantity > 0
                          select p.Symbol)
            .ToListAsync(cancellationToken);

        return shadow.Select(u => u.ToUpperInvariant())
            .Concat(legs.Select(s => UnderlyingCatalog.ParseOptionSymbol(s)?.Underlying).Where(u => u is not null).Select(u => u!))
            .Where(u => DeskAnalysts.Indices.Contains(u))
            .Distinct()
            .OrderBy(u => DeskAnalysts.Indices.ToList().IndexOf(u))
            .ToList();
    }

    protected override async Task<TechnicalFacts> FactsAsync(DateTime asOfUtc, CancellationToken cancellationToken)
    {
        var indices = new List<IndexTechnicals>();
        foreach (var (name, spot, _) in MarketBriefBuilder.Indices)
        {
            var newestFirst = await live.GetBarsUntilAsync(spot, "1m", SituationMath.MinutesToRead, asOfUtc, null, cancellationToken);
            indices.Add(Build(name, newestFirst.OrderBy(b => b.BarStartUtc).ToList(), asOfUtc));
        }

        return new TechnicalFacts
        {
            AsOfUtc = asOfUtc,
            Indices = indices,
            OpenOn = await OpenIndexOptionsAsync(db, IstTime.DateOf(asOfUtc), cancellationToken),
        };
    }

    protected override string Question(TechnicalFacts facts, DateOnly day, TimeOnly at) =>
        "Grade the setup of each index from these facts:\n" + facts.Text;

    protected override AnalystVerdict Check(string answer, TechnicalFacts facts) => CheckAnswer(answer, facts);

    /// <summary>
    /// One index's facts from its 1-minute bars (oldest first, any span; only those begun before
    /// <paramref name="asOfUtc"/>'s minute are read). Public for tests.
    /// </summary>
    public static IndexTechnicals Build(string underlying, IReadOnlyList<LiveBarResponse> minutes, DateTime asOfUtc)
    {
        var asOf = DateTime.SpecifyKind(asOfUtc, DateTimeKind.Utc);
        var cut = new DateTime(asOf.Ticks - asOf.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var bars = SituationMath.Session(minutes).Where(b => b.BarStartUtc < cut).ToList();
        if (bars.Count == 0) return new IndexTechnicals { Underlying = underlying, NotAvailable = "no session bars recorded" };

        var days = bars.GroupBy(b => IstTime.DateOf(b.BarStartUtc)).OrderBy(g => g.Key).Select(g => (Day: g.Key, Bars: g.ToList())).ToList();
        var (day, session) = days[^1];
        var prior = days.Count >= 2 ? days[^2] : default;
        bool today = day == IstTime.DateOf(asOf);
        var numbers = new FactNumbers();
        decimal Px(decimal v) => numbers.Add(v, 2);
        decimal Pct(decimal v) => numbers.Add(v, 2);
        static string DayName(DateOnly d) => d.ToString("ddd d MMM", CultureInfo.InvariantCulture);
        static string Clock(DateTime utc) => IstTime.ToIst(utc).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);

        var last = session[^1];
        decimal price = last.Close;
        decimal high = session.Max(b => b.High), low = session.Min(b => b.Low);
        var json = new JsonObject
        {
            ["underlying"] = underlying,
            ["lastBar"] = Clock(last.BarStartUtc),
            ["session"] = !today ? $"the last session ({DayName(day)}); today's has not begun"
                : TimeOnly.FromDateTime(IstTime.ToIst(asOf)) >= SituationMath.SessionClose ? "today's, closed" : "today's, in progress",
            ["price"] = Px(price),
        };

        if (prior.Bars is { Count: > 0 } before)
        {
            decimal previousClose = before[^1].Close;
            json["previousClose"] = Px(previousClose);
            if (previousClose > 0) json["changePct"] = Pct((price - previousClose) / previousClose * 100);
        }

        json["sessionBars"] = new JsonObject
        {
            ["day"] = DayName(day),
            ["open"] = Px(session[0].Open),
            ["high"] = Px(high),
            ["low"] = Px(low),
            ["range"] = Px(high - low),
        };
        if (prior.Bars is { Count: > 0 } p)
        {
            json["previousSession"] = new JsonObject
            {
                ["day"] = DayName(prior.Day),
                ["high"] = Px(p.Max(b => b.High)),
                ["low"] = Px(p.Min(b => b.Low)),
                ["close"] = Px(p[^1].Close),
            };
        }

        var window = bars.Count > SituationMath.LookbackMinutes ? bars.GetRange(bars.Count - SituationMath.LookbackMinutes, SituationMath.LookbackMinutes) : bars;
        var fives = MarketBriefBuilder.FiveMinute(window);
        var closes = fives.Select(b => (double)b.Close).ToList();
        double? ema20 = IndicatorMath.Ema(closes, 20).LastOrDefault(v => v is not null);
        double? ema50 = IndicatorMath.Ema(closes, 50).LastOrDefault(v => v is not null);
        double? atr = MarketBriefBuilder.Atr(fives, 14);
        decimal? e20 = ema20 is double a ? Px((decimal)a) : null;
        decimal? e50 = ema50 is double b50 ? Px((decimal)b50) : null;
        json["ema20"] = e20;
        json["ema50"] = e50;
        if (e20 is decimal x20 && e50 is decimal x50)
        {
            json["priceVsEmas"] = price > Math.Max(x20, x50) ? "above both" : price < Math.Min(x20, x50) ? "below both" : "between them";
            json["ema20VsEma50"] = x20 > x50 ? "above" : x20 < x50 ? "below" : "equal";
        }
        else
        {
            json["emaNote"] = $"only {fives.Count} five-minute bars: EMA 50 needs 50";
        }

        json["atr14"] = atr is double t ? numbers.Add((decimal)t, 1) : null;
        if (today)
        {
            var halfHourAgo = session.LastOrDefault(b => b.BarStartUtc <= last.BarStartUtc.AddMinutes(-30));
            if (halfHourAgo is { Close: > 0 }) json["last30MinPct"] = Pct((price - halfHourAgo.Close) / halfHourAgo.Close * 100);
        }

        json["vwap"] = "not available: an index trades no volume";

        // The levels: the sessions' extremes and closes, the EMAs, and 15-minute swings either side of the price.
        var raw = new List<(decimal Price, string What)>
        {
            (high, today ? "today's high" : $"high of {DayName(day)}"),
            (low, today ? "today's low" : $"low of {DayName(day)}"),
        };
        if (!today) raw.Add((price, $"close of {DayName(day)}"));
        if (prior.Bars is { Count: > 0 } pb)
        {
            raw.Add((pb.Max(b => b.High), $"high of {DayName(prior.Day)}"));
            raw.Add((pb.Min(b => b.Low), $"low of {DayName(prior.Day)}"));
            if (today) raw.Add((pb[^1].Close, $"previous close ({DayName(prior.Day)})"));
        }

        if (e20 is decimal l20) raw.Add((l20, "EMA 20 (5-minute)"));
        if (e50 is decimal l50) raw.Add((l50, "EMA 50 (5-minute)"));

        var fifteens = Bucket(bars, 15);
        var swings = MarketStructure.Read(
                fifteens.Select(f => new StructureBar(f.BarStartUtc, f.Open, f.High, f.Low, f.Close)).ToList(), SwingMethod.Fractal, 2)
            .Swings;
        raw.AddRange(swings.Where(s => s.Kind == SwingKind.High && s.Price > price).OrderBy(s => s.Price - price).Take(SwingsEachSide)
            .Select(s => (s.Price, $"swing high (15-minute) {Clock(s.TimeUtc)}")));
        raw.AddRange(swings.Where(s => s.Kind == SwingKind.Low && s.Price < price).OrderBy(s => price - s.Price).Take(SwingsEachSide)
            .Select(s => (s.Price, $"swing low (15-minute) {Clock(s.TimeUtc)}")));

        // One level per price: a swing at the session's high is the session's high.
        var kept = new List<(decimal Price, string What)>();
        foreach (var level in raw)
        {
            if (kept.Any(k => Math.Abs(k.Price - level.Price) <= Math.Max(0.05m, k.Price * 0.0001m))) continue;
            kept.Add((Math.Round(level.Price, 2, MidpointRounding.AwayFromZero), level.What));
        }

        var levels = kept.OrderByDescending(k => k.Price)
            .Select((k, i) => new TechnicalLevel($"L{i + 1}", k.Price, k.What, k.Price > price ? "above" : k.Price < price ? "below" : "at",
                Math.Round(k.Price - price, 2, MidpointRounding.AwayFromZero)))
            .ToList();
        json["levels"] = new JsonArray(levels.Select(l => (JsonNode)new JsonObject
        {
            ["id"] = l.Id,
            ["price"] = Px(l.Price),
            ["what"] = l.What,
            ["side"] = l.Side,
            ["distance"] = numbers.Add(l.Distance, 2),
        }).ToArray());

        return new IndexTechnicals { Underlying = underlying, Json = json, Levels = levels, Numbers = numbers };
    }

    /// <summary>
    /// The answer checked: each index with facts graded once, with a listed bias and grade; 1 to
    /// <see cref="MaxKeyLevels"/> key levels, each one of that index's levels; and no number in a reason or the summary
    /// that was not handed over. Public for tests.
    /// </summary>
    public static AnalystVerdict CheckAnswer(string answer, TechnicalFacts facts)
    {
        var obj = AiJson.Object(answer);
        if (obj?["indices"] is not JsonArray given) return AnalystVerdict.NotJson("not in the asked shape", answer);

        var problems = new List<string>();
        var expected = facts.Indices.Where(i => i.NotAvailable is null).ToDictionary(i => i.Underlying, StringComparer.Ordinal);
        var allNumbers = facts.AllNumbers;
        var graded = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var titles = new List<string>();
        var body = new StringBuilder();

        foreach (var item in given.OfType<JsonObject>())
        {
            string u = AiJson.Str(item, "underlying")?.ToUpperInvariant() ?? string.Empty;
            if (!expected.TryGetValue(u, out var index))
            {
                problems.Add($"{(u.Length > 0 ? u : "an index")} was graded, but no facts were handed over for it");
                continue;
            }

            if (!seen.Add(u))
            {
                problems.Add($"{u} was graded twice");
                continue;
            }

            string bias = AiJson.Str(item, "bias")?.ToLowerInvariant() ?? string.Empty;
            string grade = AiJson.Str(item, "grade")?.ToUpperInvariant() ?? string.Empty;
            string reason = AiJson.Str(item, "reason") ?? string.Empty;
            if (!Biases.Contains(bias)) problems.Add($"{u}: bias \"{bias}\" is not bullish, bearish or neutral");
            if (!Grades.Contains(grade)) problems.Add($"{u}: grade \"{grade}\" is not A, B, C or D");
            if (reason.Length == 0) problems.Add($"{u}: no reason");

            var levels = new JsonArray();
            var asked = item["keyLevels"] as JsonArray;
            if (asked is null || asked.Count is 0 or > MaxKeyLevels)
            {
                problems.Add($"{u}: key levels must be 1 to {MaxKeyLevels} prices from its levels");
            }

            foreach (var node in asked ?? new JsonArray())
            {
                // A level may come as a price, or as {"price": …}.
                var price = FactNumbers.Of(node is JsonObject o ? o["price"] : node);
                var level = price is decimal p ? index.Levels.FirstOrDefault(l => new FactNumbers().Also(l.Price, 2).Allows(p)) : null;
                if (level is null)
                {
                    problems.Add($"{u}: key level {node?.ToJsonString() ?? "null"} is not one of its levels");
                    continue;
                }

                levels.Add(new JsonObject { ["id"] = level.Id, ["price"] = level.Price, ["what"] = level.What, ["side"] = level.Side });
            }

            foreach (var n in allNumbers.Unsupported(reason)) problems.Add($"{u}: the reason states {n}, which is not in the facts");

            graded.Add(new JsonObject
            {
                ["underlying"] = u,
                ["bias"] = bias,
                ["grade"] = grade,
                ["keyLevels"] = levels,
                ["reason"] = reason,
            });
            titles.Add($"{u} {bias} {grade}");
            string shown = string.Join(" / ", levels.OfType<JsonObject>().Select(l => $"{FactNumbers.Of(l["price"])?.ToString("#,##0.##", CultureInfo.InvariantCulture)} ({AiJson.Str(l, "what")})"));
            body.Append($"**{u}**: {bias}, grade {grade}").Append(shown.Length > 0 ? $". Key levels: {shown}" : string.Empty)
                .Append(reason.Length > 0 ? $".\n{reason}\n\n" : ".\n\n");
        }

        foreach (var missing in expected.Keys.Where(k => !seen.Contains(k))) problems.Add($"{missing} was not graded");

        string summary = AiJson.Str(obj, "summary") ?? string.Empty;
        foreach (var n in allNumbers.Unsupported(summary)) problems.Add($"the summary states {n}, which is not in the facts");
        if (summary.Length > 0) body.Append(summary).Append('\n');
        foreach (var skipped in facts.Indices.Where(i => i.NotAvailable is not null)) body.Append($"\n_{skipped.Underlying}: not graded, {skipped.NotAvailable}._");

        var data = new JsonObject { ["indices"] = graded, ["summary"] = summary };
        string title = titles.Count > 0 ? string.Join(" · ", titles) : "no index graded";
        return problems.Count == 0
            ? new AnalystVerdict(AiReportStatus.Ok, title, body.ToString().TrimEnd(), data, string.Empty)
            : new AnalystVerdict(AiReportStatus.Invalid, title, body.ToString().TrimEnd(), data, string.Join("; ", problems));
    }

    /// <summary>1-minute bars rolled into <paramref name="minutes"/>-minute ones on the IST grid (09:15, 09:30, …), oldest first.</summary>
    public static List<LiveBarResponse> Bucket(IEnumerable<LiveBarResponse> minutesOldestFirst, int minutes) =>
        minutesOldestFirst
            .GroupBy(b => new DateTime(b.BarStartUtc.Ticks - b.BarStartUtc.Ticks % TimeSpan.FromMinutes(minutes).Ticks, DateTimeKind.Utc))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var ordered = g.OrderBy(b => b.BarStartUtc).ToList();
                return new LiveBarResponse
                {
                    Symbol = ordered[0].Symbol, Resolution = $"{minutes}m", BarStartUtc = g.Key, Open = ordered[0].Open,
                    High = ordered.Max(b => b.High), Low = ordered.Min(b => b.Low), Close = ordered[^1].Close,
                };
            })
            .ToList();
}
