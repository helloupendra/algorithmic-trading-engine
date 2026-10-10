using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.MarketFactors;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.AiAgents;

/// <summary>One market factor, as code wrote it for the Macro Analyst: an id it cites, and the fact in words with its numbers.</summary>
public sealed record MacroFact(string Id, string Text);

/// <summary>The Macro Analyst's facts: the market factors recorded by the slot, each with an id.</summary>
public sealed class MacroFacts : IAnalystFacts
{
    public required DateTime AsOfUtc { get; init; }

    public required IReadOnlyList<MacroFact> Facts { get; init; }

    /// <summary>Every number the facts state, as written in them.</summary>
    public FactNumbers Numbers => Facts.Aggregate(new FactNumbers(), (all, f) => all.AddFrom(f.Text));

    /// <summary>Whether any fact carries a number: a morning with nothing recorded is not worth a model call.</summary>
    public bool HasNumbers => Numbers.Count > 0;

    public JsonNode Data => new JsonObject
    {
        ["asOf"] = IstTime.ToIst(AsOfUtc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        ["facts"] = new JsonArray(Facts.Select(f => (JsonNode)new JsonObject { ["id"] = f.Id, ["fact"] = f.Text }).ToArray()),
    };

    public string Text =>
        $"MARKET FACTORS, {IstTime.ToIst(AsOfUtc).ToString("ddd d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)} IST, as the desk recorded " +
        "them (code wrote each line; copy numbers as written, cite facts by id):\n" +
        new JsonArray(Facts.Select(f => (JsonNode)new JsonObject { ["id"] = f.Id, ["fact"] = f.Text }).ToArray())
            .ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public string? Nothing => HasNumbers ? null : "No market factor was recorded this morning: no GIFT Nifty, overseas close, cash flow or India VIX.";
}

/// <summary>
/// The Macro Analyst (owner, 10 Oct 2026): at 08:45 IST on trading days it turns the market factors the desk recorded
/// into a bias band for the day and a position-size modifier between 0.5 and 1.0, naming the facts it rests on.
/// </summary>
/// <remarks>
/// <para>
/// The facts are the Market factors page's data, as of the moment it runs: GIFT Nifty's latest morning snapshot
/// (NSE IX) and its distance from NIFTY's previous close; this morning's snapshots of the S&amp;P 500 futures and the
/// Asian indices; the overseas markets' last closes (only bars dated before today, which have closed by then:
/// <see cref="MarketGlobalDaily"/>); the latest FII/FPI and DII cash flows and FII's last five sessions; India VIX's
/// last close; and today's events, holidays and expiries (<see cref="MarketFactorsQueries.EventsAsync"/>).
/// </para>
/// <para>
/// Code checks the answer: a listed bias, the facts it cites by id all handed over, and every number in its reason one
/// the facts state (<see cref="FactNumbers"/>). The size modifier is clamped to 0.5–1.0 whatever the model asked, and
/// the report says when it was.
/// </para>
/// </remarks>
public sealed class MacroAnalystAgent(
    TradingDbContext db,
    ILiveDataService live,
    MarketFactorsQueries factors,
    AiGateway gateway,
    AiReportWriter reports,
    IMarketSessionService sessions,
    ILogger<MacroAnalystAgent> logger,
    TimeProvider? time = null) : SlotAnalystAgent<MacroFacts>(gateway, reports, sessions, logger, time)
{
    /// <summary>The five bias bands, bearish to bullish.</summary>
    public static readonly IReadOnlyList<string> Bands = ["bearish", "lean bearish", "neutral", "lean bullish", "bullish"];

    public const decimal MinSize = 0.5m;
    public const decimal MaxSize = 1.0m;

    /// <summary>08:45, written until the 09:15 open: a morning bias after the open is no longer a morning bias.</summary>
    public static readonly IReadOnlyList<AnalystSlot> FixedSlots = [new(new TimeOnly(8, 45), TimeSpan.FromMinutes(30))];

    /// <summary>The overseas closes read, in the order the facts list them.</summary>
    public static readonly IReadOnlyList<string> GlobalCloses =
        ["SPX", "NDX", "DJI", "VIX", "N225", "HSI", "KS11", "STOXX50E", "FTSE", "BRENT", "GOLD", "DXY", "US10Y", "USDINR"];

    /// <summary>The morning snapshots read besides GIFT Nifty: what trades while India has not opened.</summary>
    public static readonly IReadOnlyList<string> MorningQuotes = ["ES", "N225", "HSI", "KS11"];

    public override string AgentKey => AiCatalog.MacroAnalyst;

    public override IReadOnlyList<AnalystSlot> Slots => FixedSlots;

    protected override string Label => "Macro";

    protected override Task<MacroFacts> FactsAsync(DateTime asOfUtc, CancellationToken cancellationToken) =>
        BuildAsync(db, live, factors, asOfUtc, cancellationToken);

    protected override string Question(MacroFacts facts, DateOnly day, TimeOnly at) =>
        "Give the day's bias band and size modifier from these facts:\n" + facts.Text;

    protected override AnalystVerdict Check(string answer, MacroFacts facts) => CheckAnswer(answer, facts);

    /// <summary>The facts as of <paramref name="asOfUtc"/>. Public for tests; <paramref name="factors"/> may be null (no events read).</summary>
    public static async Task<MacroFacts> BuildAsync(TradingDbContext db, ILiveDataService live, MarketFactorsQueries? factors, DateTime asOfUtc, CancellationToken cancellationToken)
    {
        var asOf = DateTime.SpecifyKind(asOfUtc, DateTimeKind.Utc);
        var day = IstTime.DateOf(asOf);
        var dayStart = IstTime.StartOfDayUtc(day);
        var lines = new List<string>();
        static string N(decimal v, int places = 2) => Math.Round(v, places, MidpointRounding.AwayFromZero).ToString(places == 0 ? "0" : "0." + new string('#', places), CultureInfo.InvariantCulture);
        static string Signed(decimal v, int places = 2) => (v > 0 ? "+" : string.Empty) + N(v, places);
        static string Clock(DateTime utc) => IstTime.ToIst(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToString("HH:mm", CultureInfo.InvariantCulture);
        static string DayName(DateOnly d) => d.ToString("ddd d MMM", CultureInfo.InvariantCulture);
        string Name(string key) => GlobalMarketKeys.All.FirstOrDefault(g => g.Key == key)?.Name ?? key;

        // NIFTY's last close: what GIFT Nifty is read against.
        decimal? niftyClose = null;
        var nifty = await LastClosesAsync(live, MarketBriefBuilder.Indices[0].Spot, asOf, cancellationToken);
        if (nifty.Count > 0)
        {
            niftyClose = nifty[^1].Close;
            lines.Add($"NIFTY's last close ({DayName(nifty[^1].Day)}): {N(nifty[^1].Close)}" +
                      (nifty.Count > 1 && nifty[^2].Close > 0 ? $" ({Signed((nifty[^1].Close - nifty[^2].Close) / nifty[^2].Close * 100)}% on that session)." : "."));
        }

        var snapshots = await db.MarketQuoteSnapshots.AsNoTracking()
            .Where(s => s.FetchedUtc >= dayStart && s.FetchedUtc <= asOf)
            .ToListAsync(cancellationToken);
        var gift = snapshots.Where(s => s.Key == GlobalMarketKeys.GiftNifty).OrderBy(s => s.FetchedUtc).LastOrDefault();
        if (gift is null)
        {
            lines.Add("GIFT Nifty this morning: not recorded.");
        }
        else
        {
            string text = $"GIFT Nifty this morning: {N(gift.Price)}" + (gift.ChangePct is decimal c ? $" ({Signed(c)}% against its own previous close)" : string.Empty) +
                          $", taken {Clock(gift.FetchedUtc)} IST";
            if (niftyClose is decimal nc && nc > 0)
            {
                text += $"; {Signed(gift.Price - nc)} points ({Signed((gift.Price - nc) / nc * 100)}%) from NIFTY's last close (a future: part of that is its premium)";
            }

            lines.Add(text + ".");
        }

        foreach (var key in MorningQuotes)
        {
            var s = snapshots.Where(x => x.Key == key).OrderBy(x => x.FetchedUtc).LastOrDefault();
            if (s is null) continue;
            lines.Add($"{Name(key)} this morning: {N(s.Price)}" + (s.ChangePct is decimal c ? $" ({Signed(c)}% against its previous close)" : string.Empty) +
                      $", taken {Clock(s.FetchedUtc)} IST.");
        }

        // Each overseas market's last two closed bars, dated before today: the last close and its move on the day.
        var since = day.AddDays(-10);
        var closes = await db.MarketGlobalDaily.AsNoTracking()
            .Where(g => GlobalCloses.Contains(g.Symbol) && g.Date < day && g.Date >= since)
            .Select(g => new { g.Symbol, g.Date, g.Close })
            .ToListAsync(cancellationToken);
        foreach (var key in GlobalCloses)
        {
            var two = closes.Where(c => c.Symbol == key).OrderBy(c => c.Date).TakeLast(2).ToList();
            if (two.Count == 0) continue;
            var lastBar = two[^1];
            lines.Add($"{Name(key)} last close ({DayName(lastBar.Date)}): {N(lastBar.Close)}" +
                      (two.Count == 2 && two[0].Close > 0 ? $" ({Signed((lastBar.Close - two[0].Close) / two[0].Close * 100)}% on the day)." : "."));
        }

        // Cash flows: the latest day before today, and FII's last five recorded sessions.
        var flows = await db.MarketCashFlows.AsNoTracking()
            .Where(f => f.Date < day && f.Date >= day.AddDays(-15))
            .Select(f => new { f.Date, f.Category, f.NetValueCrore })
            .ToListAsync(cancellationToken);
        if (flows.Count == 0)
        {
            lines.Add("FII/DII cash flows: not recorded.");
        }
        else
        {
            var latestDay = flows.Max(f => f.Date);
            foreach (var f in flows.Where(f => f.Date == latestDay).OrderBy(f => f.Category, StringComparer.Ordinal))
            {
                lines.Add($"{f.Category} cash on {DayName(latestDay)}: net {Signed(f.NetValueCrore)} crore.");
            }

            var fii = flows.Where(f => f.Category.StartsWith("FII", StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.Date).Take(5).ToList();
            if (fii.Count >= 2)
            {
                lines.Add($"FII/FPI cash over its last {fii.Count} recorded sessions ({DayName(fii[^1].Date)} to {DayName(fii[0].Date)}): net {Signed(fii.Sum(f => f.NetValueCrore))} crore.");
            }
        }

        var vix = await LastClosesAsync(live, MarketBriefBuilder.VixSymbol, asOf, cancellationToken);
        lines.Add(vix.Count == 0
            ? "India VIX: not recorded."
            : $"India VIX's last close ({DayName(vix[^1].Day)}): {N(vix[^1].Close)}" +
              (vix.Count > 1 && vix[^2].Close > 0 ? $" ({Signed((vix[^1].Close - vix[^2].Close) / vix[^2].Close * 100)}% on that session)." : "."));

        if (factors is not null)
        {
            var events = (await factors.EventsAsync(day, day, cancellationToken)).Events
                .OrderByDescending(e => e.Importance).ThenBy(e => e.TimeIst).Take(8).ToList();
            if (events.Count == 0) lines.Add("Events today: none listed.");
            foreach (var e in events)
            {
                lines.Add($"Today{(string.IsNullOrWhiteSpace(e.TimeIst) ? string.Empty : " at " + e.TimeIst + " IST")}: {e.Title} ({e.Category}, importance {e.Importance}).");
            }
        }

        return new MacroFacts
        {
            AsOfUtc = asOf,
            Facts = lines.Select((t, i) => new MacroFact($"m{i + 1}", t)).ToList(),
        };
    }

    /// <summary>The last two regular sessions' closes of a symbol before <paramref name="asOfUtc"/>, oldest first.</summary>
    private static async Task<List<(DateOnly Day, decimal Close)>> LastClosesAsync(ILiveDataService live, string symbol, DateTime asOfUtc, CancellationToken cancellationToken)
    {
        var newestFirst = await live.GetBarsUntilAsync(symbol, "1m", SituationMath.MinutesToRead, asOfUtc, null, cancellationToken);
        var today = IstTime.DateOf(asOfUtc);
        return SituationMath.Session(newestFirst)
            .Where(b => IstTime.DateOf(b.BarStartUtc) < today)
            .GroupBy(b => IstTime.DateOf(b.BarStartUtc))
            .OrderBy(g => g.Key)
            .TakeLast(2)
            .Select(g => (g.Key, g.OrderBy(b => b.BarStartUtc).Last().Close))
            .ToList();
    }

    /// <summary>
    /// The answer checked: a listed bias band; a size modifier, clamped to 0.5–1.0; one or more facts cited by an id that
    /// was handed over; and no number in the reason or the risks that the facts do not state. Public for tests.
    /// </summary>
    public static AnalystVerdict CheckAnswer(string answer, MacroFacts facts)
    {
        var obj = AiJson.Object(answer);
        if (obj is null) return AnalystVerdict.NotJson("not in the asked shape", answer);

        var problems = new List<string>();
        string bias = AiJson.Str(obj, "bias")?.ToLowerInvariant() ?? string.Empty;
        if (!Bands.Contains(bias)) problems.Add($"bias \"{bias}\" is not one of {string.Join(", ", Bands)}");

        decimal? asked = FactNumbers.Of(obj["sizeModifier"]);
        decimal? size = asked is decimal a ? Math.Clamp(a, MinSize, MaxSize) : null;
        if (asked is null) problems.Add("no size modifier");

        var byId = facts.Facts.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
        var cited = AiJson.Strings(obj, "restsOn");
        var restsOn = new JsonArray();
        if (cited.Count == 0) problems.Add("it names no fact it rests on");
        foreach (var id in cited.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (byId.TryGetValue(id, out var fact)) restsOn.Add(new JsonObject { ["id"] = fact.Id, ["fact"] = fact.Text });
            else problems.Add($"it rests on \"{id}\", which is not one of the facts");
        }

        // Its own size modifier may be said in words too ("size 0.8").
        var allowed = facts.Numbers;
        if (asked is decimal sa) allowed.Add(sa, 2);
        if (size is decimal sc) allowed.Add(sc, 2);
        string reason = AiJson.Str(obj, "reason") ?? string.Empty;
        string risks = AiJson.Str(obj, "risks") ?? string.Empty;
        if (reason.Length == 0) problems.Add("no reason");
        foreach (var n in allowed.Unsupported(reason)) problems.Add($"the reason states {n}, which is not in the facts");
        foreach (var n in allowed.Unsupported(risks)) problems.Add($"the risks state {n}, which is not in the facts");

        string sizeText = size is decimal s ? s.ToString("0.00", CultureInfo.InvariantCulture) : "?";
        var data = new JsonObject
        {
            ["bias"] = bias,
            ["sizeModifier"] = size,
            ["sizeModifierAsked"] = asked is decimal x && size is decimal y && x != y ? x : null,
            ["restsOn"] = restsOn,
            ["reason"] = reason,
            ["risks"] = risks,
        };

        var body = new StringBuilder($"**Bias:** {bias} · **size ×{sizeText}**");
        if (asked is decimal ax && size is decimal sx && ax != sx) body.Append($" (it asked {ax.ToString(CultureInfo.InvariantCulture)}; clamped to 0.5–1.0)");
        body.Append("\n\n").Append(reason).Append('\n');
        if (restsOn.Count > 0) body.Append("\n**Rests on**\n").Append(string.Concat(restsOn.OfType<JsonObject>().Select(f => $"- {AiJson.Str(f, "id")}: {AiJson.Str(f, "fact")}\n")));
        if (risks.Length > 0) body.Append("\n**Would change the view:** ").Append(risks).Append('\n');

        string title = $"{(bias.Length > 0 ? bias : "?")}, size ×{sizeText}";
        return problems.Count == 0
            ? new AnalystVerdict(AiReportStatus.Ok, title, body.ToString().TrimEnd(), data, string.Empty)
            : new AnalystVerdict(AiReportStatus.Invalid, title, body.ToString().TrimEnd(), data, string.Join("; ", problems));
    }
}
