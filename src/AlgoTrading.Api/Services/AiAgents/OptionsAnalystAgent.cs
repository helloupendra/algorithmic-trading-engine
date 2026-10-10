using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.OptionChain;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Api.Services.AiAgents;

/// <summary>One index's chain facts, computed by code from the recorded chain; or why there are none.</summary>
public sealed class IndexChainFacts
{
    public required string Underlying { get; init; }

    /// <summary>Why the index has no chain to read; null when it has.</summary>
    public string? NotAvailable { get; init; }

    public JsonObject Json { get; init; } = new();

    /// <summary>Every number in <see cref="Json"/>, as written there.</summary>
    public FactNumbers Numbers { get; init; } = new();
}

/// <summary>The Options Analyst's facts: each index's nearest-expiry chain.</summary>
public sealed class OptionsFacts : IAnalystFacts
{
    public required DateTime AsOfUtc { get; init; }

    public required IReadOnlyList<IndexChainFacts> Indices { get; init; }

    public JsonNode Data => new JsonObject
    {
        ["asOf"] = IstTime.ToIst(AsOfUtc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        ["indices"] = new JsonArray(Indices.Select(i => i.NotAvailable is null
            ? (JsonNode)i.Json.DeepClone()
            : new JsonObject { ["underlying"] = i.Underlying, ["notAvailable"] = i.NotAvailable }).ToArray()),
    };

    public string Text =>
        $"OPTION CHAIN FACTS, {IstTime.ToIst(AsOfUtc).ToString("ddd d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)} IST. Computed by the " +
        "desk's code from its recorded chains (nearest expiry; OI in lakh of contracts' quantity as recorded). Copy numbers " +
        "from here.\n" + Data.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public string? Nothing => Indices.All(i => i.NotAvailable is not null)
        ? "No index has a chain to read: " + string.Join("; ", Indices.Select(i => $"{i.Underlying}: {i.NotAvailable}"))
        : null;

    public FactNumbers AllNumbers => Indices.Aggregate(new FactNumbers(), (all, i) => all.With(i.Numbers));
}

/// <summary>
/// The Options/OI Analyst (owner, 10 Oct 2026): at 08:30, 10:30, 13:00 and 14:30 IST on trading days it reads the
/// nearest-expiry chains of NIFTY, BANKNIFTY and SENSEX as the desk recorded them and writes a few lines per index.
/// </summary>
/// <remarks>
/// <para>
/// The numbers are the option chain page's (<see cref="OptionChainService.GetViewAsync"/> as of the slot, the recorded
/// captures only, and <see cref="OptionChainService.GetTrendAsync"/> for the session's OI history): PCR and the PCR of
/// the day's OI change, max pain, the two heaviest call and put strikes, total OI and its change, ATM IV, days to
/// expiry; and, against the session's first capture, the change in PCR, OI and ATM IV. Distances from the spot to max
/// pain and to the walls are computed here, so the model never has to.
/// </para>
/// <para>
/// At 08:30 the newest capture is the previous session's last; the facts say so. Every number in the model's lines
/// must be one of that index's facts (<see cref="FactNumbers"/>), or the report is invalid.
/// </para>
/// </remarks>
public sealed class OptionsAnalystAgent(
    OptionChainService chains,
    AiGateway gateway,
    AiReportWriter reports,
    IMarketSessionService sessions,
    ILogger<OptionsAnalystAgent> logger,
    TimeProvider? time = null) : SlotAnalystAgent<OptionsFacts>(gateway, reports, sessions, logger, time)
{
    public static readonly IReadOnlySet<string> Stances = new HashSet<string>(StringComparer.Ordinal) { "bullish", "bearish", "neutral", "range-bound" };

    /// <summary>08:30 (until the 09:15 open), 10:30, 13:00 and 14:30, each written until the next or the close.</summary>
    public static readonly IReadOnlyList<AnalystSlot> FixedSlots =
    [
        new(new TimeOnly(8, 30), TimeSpan.FromMinutes(45)),
        new(new TimeOnly(10, 30), TimeSpan.FromMinutes(150)),
        new(new TimeOnly(13, 0), TimeSpan.FromMinutes(90)),
        new(new TimeOnly(14, 30), TimeSpan.FromMinutes(60)),
    ];

    public const int MaxLines = 4;

    public override string AgentKey => AiCatalog.OptionsAnalyst;

    public override IReadOnlyList<AnalystSlot> Slots => FixedSlots;

    protected override string Label => "Options";

    protected override async Task<OptionsFacts> FactsAsync(DateTime asOfUtc, CancellationToken cancellationToken)
    {
        var indices = new List<IndexChainFacts>();
        foreach (var name in DeskAnalysts.Indices)
        {
            indices.Add(await BuildAsync(chains, name, asOfUtc, cancellationToken));
        }

        return new OptionsFacts { AsOfUtc = asOfUtc, Indices = indices };
    }

    protected override string Question(OptionsFacts facts, DateOnly day, TimeOnly at) =>
        "Read each index's option chain from these facts:\n" + facts.Text;

    protected override AnalystVerdict Check(string answer, OptionsFacts facts) => CheckAnswer(answer, facts);

    /// <summary>One index's chain facts as of <paramref name="asOfUtc"/>, from the recorded captures. Public for tests.</summary>
    public static async Task<IndexChainFacts> BuildAsync(OptionChainService chains, string underlying, DateTime asOfUtc, CancellationToken cancellationToken)
    {
        var view = await chains.GetViewAsync(underlying, null, asOfUtc, cancellationToken);
        if (view.Strikes.Count == 0) return new IndexChainFacts { Underlying = underlying, NotAvailable = "no option chain recorded" };

        var header = view.Header;
        var numbers = new FactNumbers();
        static decimal Lakh(long oi) => oi / 100_000m;
        static string Clock(DateTime utc) => IstTime.ToIst(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);
        bool today = IstTime.DateOf(view.AsOfUtc) == IstTime.DateOf(asOfUtc);
        decimal spot = numbers.Add(view.SpotPrice, 2);

        var json = new JsonObject
        {
            ["underlying"] = underlying,
            ["capture"] = Clock(view.AsOfUtc),
            ["captureNote"] = today ? "today's latest capture" : "the last capture of an earlier session: today's chain is not recorded yet",
            ["expiry"] = view.ExpiryDate.ToString("d MMM yyyy", CultureInfo.InvariantCulture),
            ["daysToExpiry"] = header?.DaysToExpiry is int dte ? numbers.Add(dte, 0) : null,
            ["spot"] = spot,
            ["atmStrike"] = numbers.Add(header?.AtTheMoneyStrike ?? view.AtTheMoneyStrike, 0),
            ["pcr"] = numbers.Add(header?.PutCallRatio ?? view.PutCallRatio, 2),
            ["pcrOfOiChange"] = numbers.Add(header?.PutCallRatioOfChange, 2),
        };

        decimal? maxPain = header?.MaxPainStrike ?? view.MaxPainStrike;
        if (maxPain is decimal mp)
        {
            json["maxPain"] = numbers.Add(mp, 0);
            json["spotMinusMaxPain"] = numbers.Add(spot - mp, 2);
        }

        var calls = view.Strikes.Where(s => s.Call?.OpenInterest is > 0).OrderByDescending(s => s.Call!.OpenInterest).Take(2).ToList();
        var puts = view.Strikes.Where(s => s.Put?.OpenInterest is > 0).OrderByDescending(s => s.Put!.OpenInterest).Take(2).ToList();
        json["callWalls"] = new JsonArray(calls.Select(s => (JsonNode)new JsonObject
        {
            ["strike"] = numbers.Add(s.StrikePrice, 0),
            ["oiLakh"] = numbers.Add(Lakh(s.Call!.OpenInterest!.Value), 1),
            ["distanceFromSpot"] = numbers.Add(s.StrikePrice - spot, 2),
        }).ToArray());
        json["putWalls"] = new JsonArray(puts.Select(s => (JsonNode)new JsonObject
        {
            ["strike"] = numbers.Add(s.StrikePrice, 0),
            ["oiLakh"] = numbers.Add(Lakh(s.Put!.OpenInterest!.Value), 1),
            ["distanceFromSpot"] = numbers.Add(s.StrikePrice - spot, 2),
        }).ToArray());

        long totalCall = header?.TotalCallOpenInterest ?? view.TotalCallOpenInterest;
        long totalPut = header?.TotalPutOpenInterest ?? view.TotalPutOpenInterest;
        json["totalCallOiLakh"] = numbers.Add(Lakh(totalCall), 1);
        json["totalPutOiLakh"] = numbers.Add(Lakh(totalPut), 1);
        json["callOiChangeOnDayLakh"] = numbers.Add(Lakh(header?.TotalCallOpenInterestChange ?? view.TotalCallOpenInterestChange), 1);
        json["putOiChangeOnDayLakh"] = numbers.Add(Lakh(header?.TotalPutOpenInterestChange ?? view.TotalPutOpenInterestChange), 1);
        decimal? iv = header?.AtTheMoneyIv;
        json["atmIv"] = numbers.Add(iv, 1);

        // The session's own history: the first capture of the capture's day against the latest.
        var trend = await chains.GetTrendAsync(underlying, view.ExpiryDate, asOfUtc, 400, cancellationToken);
        var points = trend.Points.Where(p => p.CapturedUtc <= view.AsOfUtc && IstTime.DateOf(p.CapturedUtc) == IstTime.DateOf(view.AsOfUtc)).ToList();
        if (points.Count >= 2)
        {
            var first = points[0];
            var latest = points[^1];
            var since = new JsonObject
            {
                ["firstCapture"] = Clock(first.CapturedUtc),
                ["pcrThen"] = numbers.Add(first.PutCallRatio, 2),
                ["pcrChange"] = first.PutCallRatio is decimal a && latest.PutCallRatio is decimal b ? numbers.Add(b - a, 2) : null,
                ["callOiChangeLakh"] = numbers.Add(Lakh(latest.CallOpenInterest - first.CallOpenInterest), 1),
                ["putOiChangeLakh"] = numbers.Add(Lakh(latest.PutOpenInterest - first.PutOpenInterest), 1),
            };
            if (first.CapturedUtc < view.AsOfUtc && iv is decimal ivNow)
            {
                var then = await chains.GetViewAsync(underlying, view.ExpiryDate, first.CapturedUtc, cancellationToken);
                if (then.Header?.AtTheMoneyIv is decimal ivThen)
                {
                    since["atmIvThen"] = numbers.Add(ivThen, 1);
                    since["atmIvChange"] = numbers.Add(Math.Round(ivNow, 1, MidpointRounding.AwayFromZero) - Math.Round(ivThen, 1, MidpointRounding.AwayFromZero), 1);
                }
            }

            json["sinceFirstCapture"] = since;
        }
        else
        {
            json["sinceFirstCapture"] = "one capture so far this session: no change to read";
        }

        return new IndexChainFacts { Underlying = underlying, Json = json, Numbers = numbers };
    }

    /// <summary>
    /// The answer checked: each index with facts read once, with a listed stance and 1 to <see cref="MaxLines"/> lines,
    /// and every number in its lines one of that index's facts; the summary's numbers any index's. Public for tests.
    /// </summary>
    public static AnalystVerdict CheckAnswer(string answer, OptionsFacts facts)
    {
        var obj = AiJson.Object(answer);
        if (obj?["indices"] is not JsonArray given) return AnalystVerdict.NotJson("not in the asked shape", answer);

        var problems = new List<string>();
        var expected = facts.Indices.Where(i => i.NotAvailable is null).ToDictionary(i => i.Underlying, StringComparer.Ordinal);
        var read = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var titles = new List<string>();
        var body = new StringBuilder();

        foreach (var item in given.OfType<JsonObject>())
        {
            string u = AiJson.Str(item, "underlying")?.ToUpperInvariant() ?? string.Empty;
            if (!expected.TryGetValue(u, out var index))
            {
                problems.Add($"{(u.Length > 0 ? u : "an index")} was read, but no chain was handed over for it");
                continue;
            }

            if (!seen.Add(u))
            {
                problems.Add($"{u} was read twice");
                continue;
            }

            string stance = AiJson.Str(item, "stance")?.ToLowerInvariant() ?? string.Empty;
            if (!Stances.Contains(stance)) problems.Add($"{u}: stance \"{stance}\" is not bullish, bearish, neutral or range-bound");
            var lines = AiJson.Strings(item, "lines");
            if (lines.Count is 0 or > MaxLines) problems.Add($"{u}: 1 to {MaxLines} lines are asked for, not {lines.Count}");
            foreach (var line in lines)
            {
                foreach (var n in index.Numbers.Unsupported(line)) problems.Add($"{u}: \"{Cut(line, 60)}\" states {n}, which is not in its facts");
            }

            read.Add(new JsonObject { ["underlying"] = u, ["stance"] = stance, ["lines"] = new JsonArray(lines.Select(l => (JsonNode)l).ToArray()) });
            titles.Add($"{u} {stance}");
            body.Append($"**{u}**: {stance}\n").Append(string.Concat(lines.Select(l => $"- {l}\n"))).Append('\n');
        }

        foreach (var missing in expected.Keys.Where(k => !seen.Contains(k))) problems.Add($"{missing} was not read");

        string summary = AiJson.Str(obj, "summary") ?? string.Empty;
        foreach (var n in facts.AllNumbers.Unsupported(summary)) problems.Add($"the summary states {n}, which is not in the facts");
        if (summary.Length > 0) body.Append(summary).Append('\n');
        foreach (var skipped in facts.Indices.Where(i => i.NotAvailable is not null)) body.Append($"\n_{skipped.Underlying}: not read, {skipped.NotAvailable}._");

        var data = new JsonObject { ["indices"] = read, ["summary"] = summary };
        string title = titles.Count > 0 ? string.Join(" · ", titles) : "no index read";
        return problems.Count == 0
            ? new AnalystVerdict(AiReportStatus.Ok, title, body.ToString().TrimEnd(), data, string.Empty)
            : new AnalystVerdict(AiReportStatus.Invalid, title, body.ToString().TrimEnd(), data, string.Join("; ", problems));
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
