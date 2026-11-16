using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiAgents;

/// <summary>What a Market Impact record says, read back from its report's data (<see cref="MarketImpactAgent.ReadFacts"/>).</summary>
/// <param name="Impact">0 to 3: how much the headline can move NIFTY.</param>
/// <param name="Nifty">Which way it pushes NIFTY: up, down, mixed or unclear; empty below impact 2.</param>
/// <param name="Volatility">Whether the day may swing more: higher, lower or same; empty below impact 2.</param>
/// <param name="RepeatOf">The earlier told record it repeats (<c>t123</c>), or null.</param>
/// <param name="What">Kya hua, in Hinglish; empty below impact 2.</param>
/// <param name="Why">India aur NIFTY pe asar kyon.</param>
/// <param name="Watch">Aage kya dekhna hai.</param>
/// <param name="Source">The publisher, as the recorder named it.</param>
/// <param name="Headline">The headline as recorded.</param>
/// <param name="FirstSeenUtc">When the desk first saw the headline.</param>
/// <param name="Stocks">The NSE stocks it pushes, each with up, down or mixed; only symbols the master lists.</param>
/// <param name="Sectors">The sectors it pushes, each with its effect.</param>
public sealed record MarketImpactFacts(
    int Impact,
    string Scope,
    IReadOnlyList<string> Channels,
    string Nifty,
    string Volatility,
    string Horizon,
    double? Confidence,
    string? RepeatOf,
    string What,
    string Why,
    string Watch,
    string Source,
    string Headline,
    DateTime? FirstSeenUtc,
    IReadOnlyList<(string Name, string Effect)>? Stocks = null,
    IReadOnlyList<(string Name, string Effect)>? Sectors = null);

/// <summary>
/// The Market Impact Analyst (owner, 16 Nov 2026: "news aayi to turant pata lage ki market pe kya asar aa sakta hai").
/// Every two minutes, around the clock, the newest headlines it has not read, each rated 0 to 3 for how much it can
/// move NIFTY; for a 2 or a 3, the channel, the push on NIFTY, the day's swing and three Hinglish lines. Ira tells the
/// owners each new 2 or 3 (<c>IraDesk</c>).
/// </summary>
/// <remarks>
/// <para>
/// Only headlines the desk first saw in the last <see cref="AiSettings.MarketImpactLookbackMinutes"/>: news, not the
/// recorders' history. Newest first, so a busy hour reaches the latest item soonest.
/// </para>
/// <para>
/// Each record is checked before it is good: an impact from 0 to 3, and for a 2 or a 3 every field from its list, the
/// three lines present and short, and every number they state found in the headline or its summary, so a number the
/// model made up cannot reach the owner. A record that fails is kept as invalid with the reason and is never told.
/// </para>
/// <para>
/// The same story arrives from several publishers. The model is handed the stories told in the last
/// <see cref="RepeatWindow"/> and names the one an item repeats; a repeat is kept, and not told.
/// </para>
/// </remarks>
public sealed partial class MarketImpactAgent(
    TradingDbContext db,
    AiGateway gateway,
    AiReportWriter reports,
    AiSchedulerState schedule,
    IOptionsMonitor<AiSettings> settings,
    ILogger<MarketImpactAgent> logger,
    TimeProvider? time = null) : IAiScheduledAgent
{
    public static readonly IReadOnlySet<string> Scopes = new HashSet<string>(StringComparer.Ordinal) { "global", "india", "sector", "company" };

    public static readonly IReadOnlySet<string> Channels = new HashSet<string>(StringComparer.Ordinal)
    {
        "oil", "rates-dollar", "rupee", "fii-flows", "global-risk", "india-policy", "geopolitics", "sector", "company", "other",
    };

    public static readonly IReadOnlySet<string> Tilts = new HashSet<string>(StringComparer.Ordinal) { "up", "down", "mixed", "unclear" };

    public static readonly IReadOnlySet<string> Volatilities = new HashSet<string>(StringComparer.Ordinal) { "higher", "lower", "same" };

    public static readonly IReadOnlySet<string> Horizons = new HashSet<string>(StringComparer.Ordinal) { "minutes", "day", "weeks" };

    /// <summary>The sectors a story may name (owner, 16 Nov 2026: "kaunse stocks pe asar padega").</summary>
    public static readonly IReadOnlySet<string> Sectors = new HashSet<string>(StringComparer.Ordinal)
    {
        "banks", "financials", "it", "oil-gas", "auto", "pharma", "fmcg", "metals", "realty", "power", "telecom", "aviation",
        "capital-goods", "cement", "chemicals", "defence", "media",
    };

    /// <summary>How a story pushes a stock or a sector.</summary>
    public static readonly IReadOnlySet<string> Effects = new HashSet<string>(StringComparer.Ordinal) { "up", "down", "mixed" };

    /// <summary>The stocks a story names at most.</summary>
    public const int MaxStocks = 5;

    /// <summary>The lowest impact that gets the full record: a 2 can move NIFTY by 0.3% to 1%.</summary>
    public const int Big = 2;

    /// <summary>The longest of the three Hinglish lines, in characters (the prompt asks for under 120).</summary>
    public const int MaxLine = 160;

    /// <summary>The told stories the model is shown to spot a repeat: a story is retold by other publishers for hours.</summary>
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromHours(6);

    /// <summary>The told stories shown at most.</summary>
    public const int RepeatShown = 12;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string AgentKey => AiCatalog.MarketImpact;

    /// <summary>One headline handed to the model: its id ("n12"), the text its numbers are checked against, and what is shown.</summary>
    public sealed record Item(string Id, string SubjectId, DateOnly Day, string Text, string Source, string Headline, DateTime FirstSeenUtc, object Payload);

    public async Task<bool> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue;
        var every = TimeSpan.FromMinutes(Math.Max(1, s.MarketImpactEveryMinutes));
        if (schedule.LastWork(AgentKey) is DateTime last && nowUtc - last < every) return false;

        var batch = await NextBatchAsync(nowUtc, cancellationToken);
        schedule.Worked(AgentKey, nowUtc);
        if (batch.Count == 0) return false;

        await AnalyseAsync(batch, nowUtc, cancellationToken);

        int size = Math.Clamp(s.MarketImpactBatchSize, 1, 20);
        int turns = Math.Clamp(s.MarketImpactBatchesPerTurn, 1, 4);
        for (int more = 1; more < turns && batch.Count >= size; more++)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            batch = await NextBatchAsync(now, cancellationToken);
            if (batch.Count == 0) break;
            await AnalyseAsync(batch, now, cancellationToken);
        }

        return true;
    }

    public async Task<AiReport?> RunForAsync(string? subjectId, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var batch = await NextBatchAsync(now, cancellationToken);
        if (batch.Count == 0) return null;
        var written = await AnalyseAsync(batch, now, cancellationToken);
        return written.FirstOrDefault();
    }

    /// <summary>The newest headlines first seen within the lookback that it has not read, newest first.</summary>
    public async Task<List<Item>> NextBatchAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue;
        var since = nowUtc.AddMinutes(-Math.Max(5, s.MarketImpactLookbackMinutes));
        // A feed that re-dates an old story makes it look new to the recorder: one published over a day ago is history.
        var oldest = nowUtc.AddDays(-1);
        int size = Math.Clamp(s.MarketImpactBatchSize, 1, 20);

        var news = await db.NewsItems.AsNoTracking()
            .Where(n => n.FirstSeenUtc >= since && (n.PublishedUtc == null || n.PublishedUtc >= oldest))
            .OrderByDescending(n => n.FirstSeenUtc)
            .ThenByDescending(n => n.Id)
            .Take(size * 4)
            .Select(n => new { n.Id, n.Source, n.Title, n.Summary, n.FirstSeenUtc })
            .ToListAsync(cancellationToken);

        var due = await reports.DueAsync(AgentKey, AiReportSubject.News, news.Select(n => Id(n.Id)).ToList(), cancellationToken);
        var items = new List<Item>();
        foreach (var n in news.Where(n => due.Contains(Id(n.Id))))
        {
            string summary = Clip(n.Summary, 400);
            var seen = DateTime.SpecifyKind(n.FirstSeenUtc, DateTimeKind.Utc);
            items.Add(new Item($"n{n.Id}", Id(n.Id), IstTime.DateOf(seen), $"{n.Title}\n{summary}", n.Source, n.Title, seen,
                new { id = $"n{n.Id}", source = n.Source, title = n.Title, summary, seenIst = IstTime.ToIst(seen).ToString("HH:mm", CultureInfo.InvariantCulture) }));
            if (items.Count >= size) break;
        }

        return items;
    }

    /// <summary>The stories told lately (impact 2 or more, not a repeat), so the model can name the one an item repeats.</summary>
    public async Task<List<(string Id, string What)>> ToldLatelyAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var since = nowUtc - RepeatWindow;
        var rows = await db.AiReports.AsNoTracking()
            .Where(r => r.AgentKey == AiCatalog.MarketImpact && r.Status == AiReportStatus.Ok && r.UpdatedUtc >= since)
            .OrderByDescending(r => r.Id)
            .Take(200)
            .Select(r => new { r.Id, r.DataJson })
            .ToListAsync(cancellationToken);

        var told = new List<(string, string)>();
        foreach (var r in rows)
        {
            if (ReadFacts(r.DataJson) is not { Impact: >= Big, RepeatOf: null } f || f.What.Length == 0) continue;
            told.Add(($"t{r.Id.ToString(CultureInfo.InvariantCulture)}", f.What));
            if (told.Count >= RepeatShown) break;
        }

        return told;
    }

    private async Task<List<AiReport>> AnalyseAsync(List<Item> batch, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var toldLately = await ToldLatelyAsync(nowUtc, cancellationToken);
        string question =
            "Already told (do not tell again; name it in repeatOf when an item is the same event):\n" +
            JsonSerializer.Serialize(toldLately.Select(t => new { id = t.Id, what = t.What }), Json) +
            "\nRate these headlines:\n" + JsonSerializer.Serialize(batch.Select(i => i.Payload), Json);
        var result = await gateway.AskAsync(new AiAskInput(
            AgentKey, null, [new AiMessage("user", question)], null, 4000, 0,
            $"impact-{batch[0].Id}", "schedule", AgentKey, null), NullAiStreamSink.Instance, cancellationToken);

        var written = new List<AiReport>();
        if (result.Outcome != AiCallOutcome.Ok)
        {
            string why = result.RefusalStatus is null ? result.Error : $"Refused: {result.Error}";
            logger.LogInformation("Market impact of {Count} headlines failed: {Error}", batch.Count, why);
            foreach (var item in batch)
            {
                written.Add(await reports.SaveAsync(AgentKey, AiReportSubject.News, item.SubjectId, item.Day, AiReportStatus.Failed, result,
                    Clip(item.Headline, 200), string.Empty, "{}", why, cancellationToken));
            }

            return written;
        }

        var byId = NewsAnalystAgent.ReadItems(result.Text);
        bool cutOff = result.FinishReason == "length";
        var toldIds = toldLately.Select(t => t.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The stocks the answer named, checked against the instrument master: a symbol NSE does not list is dropped.
        var asked = (byId?.Values ?? Enumerable.Empty<JsonObject>())
            .SelectMany(r => (r["stocks"] as JsonArray ?? []).OfType<JsonObject>())
            .Select(o => StockSymbol(AiJson.Str(o, "symbol")))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Select(s => $"NSE:{s}-EQ")
            .ToList();
        var listed = asked.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await db.Instruments.AsNoTracking().Where(i => asked.Contains(i.Symbol)).Select(i => i.Symbol).ToListAsync(cancellationToken))
                .Select(s => s[4..^3]).ToHashSet(StringComparer.Ordinal);
        foreach (var item in batch)
        {
            (string Status, string Title, string Body, string Data, string Error) checkedRecord;
            if (byId is not null && byId.TryGetValue(item.Id, out var record))
            {
                checkedRecord = Check(record, item.Text, toldIds, item.Source, item.Headline, item.FirstSeenUtc, listed);
            }
            else
            {
                string title = Clip(item.Headline, 200);
                checkedRecord = cutOff ? (AiReportStatus.Failed, title, string.Empty, "{}", "The answer was cut off at the token limit before this item.")
                    : byId is null ? (AiReportStatus.Failed, title, string.Empty, "{}", "The answer was not the JSON object asked for.")
                    : (AiReportStatus.Invalid, title, string.Empty, "{}", "The answer left this item out.");
            }

            var (status, t, body, data, error) = checkedRecord;
            written.Add(await reports.SaveAsync(AgentKey, AiReportSubject.News, item.SubjectId, item.Day, status, result, t, body, data, error, cancellationToken));
        }

        return written;
    }

    /// <summary>
    /// One record, checked against its headline's text: the impact, and for a 2 or a 3 every field from its list, the
    /// three Hinglish lines present and short, and every number they state found in the text. A repeat must name a
    /// story it was shown. Answers the report's status, title, body, data and the reason when it is invalid.
    /// </summary>
    /// <param name="listedStocks">
    /// The NSE equity symbols the instrument master lists among those the answer named; null takes any symbol of the
    /// right shape (a test without a master). A stock the master does not list is dropped, not held against the record.
    /// </param>
    public static (string Status, string Title, string Body, string Data, string Error) Check(
        JsonObject record, string sourceText, IReadOnlySet<string> toldIds, string source, string headline, DateTime firstSeenUtc,
        IReadOnlySet<string>? listedStocks = null)
    {
        var problems = new List<string>();
        double? rawImpact = Whole(record["impact"]);
        int impact = rawImpact is double v && v == Math.Floor(v) && v is >= 0 and <= 3 ? (int)v : -1;
        if (impact < 0) problems.Add("impact is not a whole number from 0 to 3");

        var data = new JsonObject
        {
            ["impact"] = impact < 0 ? null : JsonValue.Create(impact),
            ["source"] = source,
            ["headline"] = headline,
            ["firstSeenUtc"] = DateTime.SpecifyKind(firstSeenUtc, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture),
        };

        string title = Clip(headline, 200);
        if (impact is >= 0 and < Big)
        {
            // A small item may still be another publisher's word on a story already told: it counts in the story's
            // tally. A name it was not shown is dropped, not held against the record: the rating is what matters.
            string? smallRepeat = AiJson.Str(record, "repeatOf")?.Trim();
            data["repeatOf"] = smallRepeat is not null && toldIds.Contains(smallRepeat) ? smallRepeat : null;
        }

        if (impact >= Big)
        {
            string scope = Lower(AiJson.Str(record, "scope"));
            var channels = AiJson.Strings(record, "channels").Select(c => c.Trim().ToLowerInvariant()).Where(c => c.Length > 0).Distinct().ToList();
            string nifty = Lower(AiJson.Str(record, "nifty"));
            string volatility = Lower(AiJson.Str(record, "volatility"));
            string horizon = Lower(AiJson.Str(record, "horizon"));
            double? confidence = AiJson.Num(record, "confidence");
            string what = Flat(AiJson.Str(record, "what"));
            string why = Flat(AiJson.Str(record, "why"));
            string watch = Flat(AiJson.Str(record, "watch"));
            string? repeatOf = AiJson.Str(record, "repeatOf")?.Trim();
            if (string.IsNullOrEmpty(repeatOf) || repeatOf.Equals("null", StringComparison.OrdinalIgnoreCase)) repeatOf = null;

            if (!Scopes.Contains(scope)) problems.Add($"scope \"{scope}\" is not global, india, sector or company");
            if (channels.Count == 0) problems.Add("no channel is named");
            foreach (string c in channels.Where(c => !Channels.Contains(c))) problems.Add($"channel \"{c}\" is not one of the listed channels");
            if (!Tilts.Contains(nifty)) problems.Add($"nifty \"{nifty}\" is not up, down, mixed or unclear");
            if (!Volatilities.Contains(volatility)) problems.Add($"volatility \"{volatility}\" is not higher, lower or same");
            if (!Horizons.Contains(horizon)) problems.Add($"horizon \"{horizon}\" is not minutes, day or weeks");
            if (confidence is null or < 0 or > 1) problems.Add("confidence is not a number from 0 to 1");
            foreach (var (name, line) in new[] { ("what", what), ("why", why), ("watch", watch) })
            {
                if (line.Length == 0) problems.Add($"{name} is empty");
                else if (line.Length > MaxLine) problems.Add($"{name} is longer than {MaxLine} characters");
                foreach (string n in NumbersNotIn(line, sourceText)) problems.Add($"{name} states {n}, which is not in the headline");
            }

            if (repeatOf is not null && !toldIds.Contains(repeatOf))
            {
                problems.Add($"repeatOf \"{repeatOf}\" is not one of the stories it was shown");
            }

            data["scope"] = scope;
            data["channels"] = new JsonArray(channels.Select(c => (JsonNode)c).ToArray());
            data["nifty"] = nifty;
            data["volatility"] = volatility;
            data["horizon"] = horizon;
            data["confidence"] = confidence;
            data["repeatOf"] = repeatOf;
            data["what"] = what;
            data["why"] = why;
            data["watch"] = watch;
            data["stocks"] = Named(record["stocks"], "symbol", s => StockSymbol(s) is { } sym && (listedStocks is null || listedStocks.Contains(sym)) ? sym : null, MaxStocks);
            data["sectors"] = Named(record["sectors"], "name", s => Sectors.Contains(Lower(s)) ? Lower(s) : null, Sectors.Count);
            if (what.Length > 0) title = Clip(what, 200);
        }

        var body = new StringBuilder();
        body.Append($"**Impact {(impact < 0 ? "?" : impact.ToString(CultureInfo.InvariantCulture))}** · {source} · {headline}");
        if (impact >= Big)
        {
            body.Append($"\n\nNIFTY {AiJson.Str(data, "nifty")}, volatility {AiJson.Str(data, "volatility")}, {AiJson.Str(data, "horizon")}")
                .Append($" · {string.Join(", ", AiJson.Strings(data, "channels"))}");
            if (AiJson.Str(data, "repeatOf") is string r) body.Append($" · repeat of {r}");
            string effects = string.Join(", ", new[] { "stocks", "sectors" }
                .SelectMany(k => (data[k] as JsonArray ?? []).OfType<JsonObject>())
                .Select(o => $"{AiJson.Str(o, "name")} {AiJson.Str(o, "effect")}"));
            if (effects.Length > 0) body.Append($"\n\nAsar: {effects}");
            body.Append($"\n- Kya hua: {AiJson.Str(data, "what")}\n- Kyon: {AiJson.Str(data, "why")}\n- Dekhna: {AiJson.Str(data, "watch")}");
        }

        return problems.Count == 0
            ? (AiReportStatus.Ok, title, body.ToString(), data.ToJsonString(Json), string.Empty)
            : (AiReportStatus.Invalid, title, body.ToString(), data.ToJsonString(Json), string.Join("; ", problems));
    }

    /// <summary>A report's data read back; null when it holds no impact (a failed try, or data nobody can read).</summary>
    public static MarketImpactFacts? ReadFacts(string? dataJson)
    {
        if (AiJson.Object(dataJson ?? string.Empty) is not { } d || AiJson.Num(d, "impact") is not double impact) return null;
        DateTime? seen = DateTime.TryParse(AiJson.Str(d, "firstSeenUtc"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null;
        static List<(string, string)> Pairs(JsonNode? n) => (n as JsonArray ?? []).OfType<JsonObject>()
            .Select(o => (AiJson.Str(o, "name") ?? string.Empty, AiJson.Str(o, "effect") ?? string.Empty))
            .Where(p => p.Item1.Length > 0).ToList();
        return new MarketImpactFacts((int)impact, AiJson.Str(d, "scope") ?? string.Empty, AiJson.Strings(d, "channels").ToList(),
            AiJson.Str(d, "nifty") ?? string.Empty, AiJson.Str(d, "volatility") ?? string.Empty, AiJson.Str(d, "horizon") ?? string.Empty,
            AiJson.Num(d, "confidence"), AiJson.Str(d, "repeatOf"), AiJson.Str(d, "what") ?? string.Empty, AiJson.Str(d, "why") ?? string.Empty,
            AiJson.Str(d, "watch") ?? string.Empty, AiJson.Str(d, "source") ?? string.Empty, AiJson.Str(d, "headline") ?? string.Empty, seen,
            Pairs(d["stocks"]), Pairs(d["sectors"]));
    }

    /// <summary>
    /// The numbers <paramref name="line"/> states that the headline's text does not: digits compared without their
    /// grouping commas. Names with digits in them (NIFTY 50, S&amp;P 500) and a number written straight after letters
    /// (Q2, FY27, G20) are not stated numbers.
    /// </summary>
    public static IEnumerable<string> NumbersNotIn(string line, string sourceText)
    {
        string stripped = NamedIndex().Replace(line, " ");
        string haystack = Digits(sourceText);
        foreach (Match m in Number().Matches(stripped))
        {
            string n = m.Value.Replace(",", string.Empty).TrimEnd('.');
            if (n.Length == 0) continue;
            if (!Regex.IsMatch(haystack, $@"(?<![\d.]){Regex.Escape(n)}(?![\d])")) yield return m.Value;
        }
    }

    /// <summary>The text with grouping commas taken out of its numbers, so 1,200 in a line matches 1200 in a headline and back.</summary>
    private static string Digits(string text) => GroupComma().Replace(text, string.Empty);

    [GeneratedRegex(@"(?<=\d),(?=\d)")]
    private static partial Regex GroupComma();

    [GeneratedRegex(@"(?<![\p{L}\d.])\d[\d,]*(?:\.\d+)?")]
    private static partial Regex Number();

    [GeneratedRegex(@"\b(?:NIFTY|Nifty|nifty)\s?(?:50|100|500)\b|S&P\s?500|Nasdaq\s?100|FTSE\s?100|Nikkei\s?225|DAX\s?40|Sensex\s?30", RegexOptions.IgnoreCase)]
    private static partial Regex NamedIndex();

    /// <summary>
    /// A list of {name, effect} from the answer, kept only where <paramref name="accept"/> gives a name and the effect is
    /// one of <see cref="Effects"/>, each name once, at most <paramref name="max"/>: <c>[{"name": "ONGC", "effect": "down"}]</c>.
    /// </summary>
    private static JsonArray Named(JsonNode? node, string key, Func<string, string?> accept, int max)
    {
        var kept = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in (node as JsonArray ?? []).OfType<JsonObject>())
        {
            if (kept.Count >= max) break;
            string effect = Lower(AiJson.Str(o, "effect"));
            if (AiJson.Str(o, key) is not string raw || accept(raw) is not string name || !Effects.Contains(effect) || !seen.Add(name)) continue;
            kept.Add(new JsonObject { ["name"] = name, ["effect"] = effect });
        }

        return kept;
    }

    /// <summary>"RELIANCE" from "RELIANCE", "NSE:RELIANCE-EQ" or "reliance"; null when it is not the shape of an NSE symbol.</summary>
    public static string? StockSymbol(string? raw)
    {
        string s = (raw ?? string.Empty).Trim().ToUpperInvariant();
        if (s.StartsWith("NSE:", StringComparison.Ordinal)) s = s[4..];
        if (s.EndsWith("-EQ", StringComparison.Ordinal)) s = s[..^3];
        return Regex.IsMatch(s, @"^[A-Z0-9&][A-Z0-9&\-]{0,19}$") ? s : null;
    }

    /// <summary>
    /// A number however the node holds it: parsed from an answer (any number), built in code (an int), or written as
    /// text, which a model does now and then ("2"): the same answer.
    /// </summary>
    private static double? Whole(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue(out double d)) return d;
        if (v.TryGetValue(out int i)) return i;
        if (v.TryGetValue(out long l)) return l;
        return v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out double t) ? t : null;
    }

    private static string Lower(string? text) => (text ?? string.Empty).Trim().ToLowerInvariant();

    private static string Flat(string? text) => Regex.Replace((text ?? string.Empty).Replace('\n', ' ').Replace('\r', ' '), @"\s+", " ").Trim();

    private static string Clip(string? text, int max)
    {
        string t = (text ?? string.Empty).Trim();
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
}
