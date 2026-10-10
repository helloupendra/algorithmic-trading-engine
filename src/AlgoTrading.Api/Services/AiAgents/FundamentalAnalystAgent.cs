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

/// <summary>
/// The Fundamental Analyst (owner, 10 Oct 2026): when a results filing of a stock in the desk's F&amp;O universe arrives,
/// it takes out revenue, profit, margins and their year-on-year changes, each number with the exact words it came from,
/// and the surprise direction those changes show.
/// </summary>
/// <remarks>
/// <para>
/// Every <see cref="AiSettings.FundamentalEveryMinutes"/> (10) it polls the corporate filings recorder's rows of the last
/// <see cref="AiSettings.NewsLookbackHours"/> (24), as the News Analyst does, never the backfills. A filing is read only
/// when code finds it is a results filing (<see cref="IsResultsFiling"/>) of a stock with a live stock future in the
/// instrument master or the F&amp;O bhavcopy (<see cref="FnoStocksAsync"/>); every other filing is skipped and no report
/// is written for it. At most <see cref="AiSettings.FundamentalBatchSize"/> (3) filings go in one call.
/// </para>
/// <para>
/// The check is the News Analyst's, made stricter: each metric's quote must be in the filing's text, and the metric's
/// value must be a number written in that quote; a year-earlier figure the same with its own quote; a year-on-year
/// change must be written in the text or equal the change code computes from the two figures. A surprise other than
/// "unclear" needs one metric with a year-on-year change behind it.
/// </para>
/// </remarks>
public sealed partial class FundamentalAnalystAgent(
    TradingDbContext db,
    AiGateway gateway,
    AiReportWriter reports,
    AiSchedulerState schedule,
    IOptionsMonitor<AiSettings> settings,
    ILogger<FundamentalAnalystAgent> logger,
    TimeProvider? time = null) : IAiScheduledAgent
{
    public static readonly IReadOnlySet<string> Metrics = new HashSet<string>(StringComparer.Ordinal)
    {
        "revenue", "net profit", "ebitda", "ebitda margin", "net margin", "eps", "net interest income", "gross npa", "other",
    };

    public static readonly IReadOnlySet<string> Surprises = new HashSet<string>(StringComparer.Ordinal) { "positive", "negative", "inline", "unclear" };

    /// <summary>Characters of a filing's details the model is handed: results summaries run longer than a headline.</summary>
    public const int DetailsChars = 3000;

    /// <summary>How close a stated year-on-year change must be to the one computed from the two figures, in points.</summary>
    public const decimal YoyTolerance = 0.15m;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string AgentKey => AiCatalog.FundamentalAnalyst;

    /// <summary>One filing handed to the model: its id ("f12"), its text, and where its report goes.</summary>
    public sealed record Item(string Id, long FilingId, string Symbol, string Company, DateOnly Day, string Text, object Payload);

    public async Task<bool> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var every = TimeSpan.FromMinutes(Math.Max(1, settings.CurrentValue.FundamentalEveryMinutes));
        if (schedule.LastWork(AgentKey) is DateTime last && nowUtc - last < every) return false;

        var batch = await NextBatchAsync(nowUtc, cancellationToken);
        schedule.Worked(AgentKey, nowUtc);
        if (batch.Count == 0) return false;

        await AnalyseAsync(batch, cancellationToken);
        return true;
    }

    /// <summary>"Run now": one filing by its id (it must be a results filing of an F&amp;O stock), or the next batch.</summary>
    public async Task<AiReport?> RunForAsync(string? subjectId, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        List<Item> batch;
        if (long.TryParse(subjectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
        {
            var filing = await db.CorporateAnnouncements.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
            if (filing is null || !IsResultsFiling(filing.Subject, filing.Details, filing.Topics)) return null;
            batch = [ItemOf(filing)];
        }
        else
        {
            batch = await NextBatchAsync(now, cancellationToken);
        }

        if (batch.Count == 0) return null;
        return (await AnalyseAsync(batch, cancellationToken)).FirstOrDefault();
    }

    /// <summary>
    /// The next results filings to read: of an F&amp;O stock, recorded in the lookback and not yet read (or failed and
    /// due again), newest first.
    /// </summary>
    public async Task<List<Item>> NextBatchAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue;
        var since = nowUtc.AddHours(-Math.Max(1, s.NewsLookbackHours));
        int size = Math.Clamp(s.FundamentalBatchSize, 1, 10);

        var filings = await db.CorporateAnnouncements.AsNoTracking()
            .Where(a => (a.AnnouncedUtc ?? a.FirstSeenUtc) >= since && a.FirstSeenUtc >= since)
            .OrderByDescending(a => a.AnnouncedUtc ?? a.FirstSeenUtc)
            .Take(500)
            .ToListAsync(cancellationToken);
        var results = filings.Where(f => IsResultsFiling(f.Subject, f.Details, f.Topics)).ToList();
        if (results.Count == 0) return [];

        var universe = await FnoStocksAsync(db, IstTime.DateOf(nowUtc), cancellationToken);
        var mine = results.Where(f => universe.Contains(f.Symbol.Trim().ToUpperInvariant())).ToList();
        var due = await reports.DueAsync(AgentKey, AiReportSubject.Filing, mine.Select(f => Id(f.Id)).ToList(), cancellationToken);
        return mine.Where(f => due.Contains(Id(f.Id))).Take(size).Select(ItemOf).ToList();
    }

    private static Item ItemOf(CorporateAnnouncement f)
    {
        string details = Clip(f.Details, DetailsChars);
        return new Item($"f{f.Id}", f.Id, f.Symbol, f.Company, IstTime.DateOf(f.AnnouncedUtc ?? f.FirstSeenUtc), FilingText(f.Subject, f.Details),
            new { id = $"f{f.Id}", symbol = f.Symbol, company = f.Company, subject = f.Subject, details });
    }

    /// <summary>The text a results filing's quotes are checked against: its subject and its details as handed over. The chat reads the same.</summary>
    public static string FilingText(string subject, string? details) => $"{subject}\n{Clip(details, DetailsChars)}";

    /// <summary>
    /// Whether a filing is results: its subject says "financial result" (NSE's subject for results), or the scorer
    /// tagged it "results", or a board meeting's outcome says it approved the results. Never a trading-window notice, a
    /// board meeting intimation, or other paperwork that only mentions results (the scorer's own paperwork rule).
    /// </summary>
    public static bool IsResultsFiling(string? subject, string? details, string? topics)
    {
        string s = (subject ?? string.Empty).ToLowerInvariant();
        if (Paperwork.Any(p => s.Contains(p, StringComparison.Ordinal))) return false;
        if (s.Contains("financial result", StringComparison.Ordinal)) return true;
        if ((topics ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains("results", StringComparer.OrdinalIgnoreCase)) return true;
        return (s.Contains("outcome of board", StringComparison.Ordinal) || s.Contains("integrated filing", StringComparison.Ordinal))
               && ResultsWords().IsMatch(details ?? string.Empty);
    }

    /// <summary>Subjects that are never results, whatever they mention (as <c>analysis/newsrules.py</c> reads them, and intimations).</summary>
    private static readonly string[] Paperwork =
    [
        "trading window", "intimation", "loss of share certificate", "duplicate share certificate", "newspaper publication",
        "74(5)", "74 (5)", "compliance certificate", "investor complaints", "analysts/institutional investor meet", "investor meet",
    ];

    [GeneratedRegex(@"\b(?:financial|quarterly|audited|unaudited|standalone|consolidated)\s+(?:financial\s+)?results?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResultsWords();

    /// <summary>
    /// The stocks of the desk's F&amp;O universe: those with a stock future still listed in the instrument master, or
    /// in the F&amp;O bhavcopy of the last ten days. Indices are not stocks.
    /// </summary>
    public static async Task<HashSet<string>> FnoStocksAsync(TradingDbContext db, DateOnly today, CancellationToken cancellationToken)
    {
        var fromMaster = await db.Instruments.AsNoTracking()
            .Where(i => i.Exchange == "NSE" && i.InstrumentType == "FUT" && i.ExpiryDate != null && i.ExpiryDate >= today)
            .Select(i => i.Underlying)
            .Distinct()
            .ToListAsync(cancellationToken);
        var since = today.AddDays(-10);
        var fromBhavcopy = await db.MarketFuturesDaily.AsNoTracking()
            .Where(f => f.InstrumentKind == "STF" && f.Date >= since)
            .Select(f => f.Underlying)
            .Distinct()
            .ToListAsync(cancellationToken);
        return fromMaster.Concat(fromBhavcopy)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim().ToUpperInvariant())
            .Where(u => !UnderlyingCatalog.IndexUnderlyings.Contains(u, StringComparer.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task<List<AiReport>> AnalyseAsync(List<Item> batch, CancellationToken cancellationToken)
    {
        string question = "Read these results filings:\n" + JsonSerializer.Serialize(batch.Select(i => i.Payload), Json);
        var result = await gateway.AskAsync(new AiAskInput(
            AgentKey, null, [new AiMessage("user", question)], null, 6000, 0,
            $"results-{batch[0].Id}", "schedule", AgentKey, null), NullAiStreamSink.Instance, cancellationToken);

        var written = new List<AiReport>();
        if (result.Outcome != AiCallOutcome.Ok)
        {
            string why = result.RefusalStatus is null ? result.Error : $"Refused: {result.Error}";
            bool capacity = AiGateway.IsCapacityRefusal(result);
            logger.LogInformation("Results extraction of {Count} filings got no answer: {Error}", batch.Count, why);
            foreach (var item in batch)
            {
                string title = $"{item.Symbol}: results not read";
                written.Add(capacity
                    ? await reports.SaveTurnedAwayAsync(AgentKey, AiReportSubject.Filing, Id(item.FilingId), item.Day, result, title,
                        $"Turned away for capacity, not counted as a try: {why}", cancellationToken)
                    : await reports.SaveAsync(AgentKey, AiReportSubject.Filing, Id(item.FilingId), item.Day, AiReportStatus.Failed, result,
                        title, string.Empty, "{}", why, cancellationToken));
            }

            return written;
        }

        var byId = NewsAnalystAgent.ReadItems(result.Text);
        bool cutOff = result.FinishReason == "length";
        foreach (var item in batch)
        {
            AnalystVerdict verdict;
            if (byId is not null && byId.TryGetValue(item.Id, out var record))
            {
                verdict = Check(record, item.Text, item.Symbol);
            }
            else if (cutOff || byId is null)
            {
                // Not read: a later poll reads it again.
                string why = cutOff ? "The answer was cut off at the token limit before this filing." : "The answer was not the JSON object asked for.";
                written.Add(await reports.SaveAsync(AgentKey, AiReportSubject.Filing, Id(item.FilingId), item.Day, AiReportStatus.Failed, result,
                    $"{item.Symbol}: results not read", string.Empty, "{}", why, cancellationToken));
                continue;
            }
            else
            {
                verdict = new AnalystVerdict(AiReportStatus.Invalid, $"{item.Symbol}: results not read", string.Empty, new JsonObject(), "The answer left this filing out.");
            }

            verdict.Data["symbol"] = item.Symbol;
            verdict.Data["company"] = item.Company;
            written.Add(await reports.SaveAsync(AgentKey, AiReportSubject.Filing, Id(item.FilingId), item.Day, verdict.Status, result,
                verdict.Title, verdict.Body, verdict.Data.ToJsonString(Json), verdict.Error, cancellationToken));
        }

        return written;
    }

    /// <summary>
    /// One filing's record, checked against the filing's text: a results flag; each metric from the list, its value
    /// written in its quote and its quote in the text; a year-earlier figure the same; a year-on-year change written in
    /// the text or equal to code's own from the two figures; a listed surprise, resting on a change unless unclear; and
    /// no number in the summary that the text and the changes do not hold. Public for tests.
    /// </summary>
    public static AnalystVerdict Check(JsonObject record, string sourceText, string symbol)
    {
        var problems = new List<string>();
        string haystack = FactNumbers.Normalise(sourceText);
        var textNumbers = new FactNumbers().AddFrom(sourceText);
        bool? isResults = record["results"] is JsonValue rv && rv.TryGetValue(out bool b) ? b : null;
        if (isResults is null) problems.Add("results is not true or false");

        string period = AiJson.Str(record, "period") ?? string.Empty;
        string surprise = AiJson.Str(record, "surprise")?.ToLowerInvariant() ?? (isResults == false ? "unclear" : string.Empty);
        string summary = AiJson.Str(record, "summary") ?? string.Empty;

        if (isResults == false)
        {
            var skipped = new JsonObject { ["results"] = false, ["summary"] = summary };
            return problems.Count == 0
                ? new AnalystVerdict(AiReportStatus.Ok, $"{symbol}: not the results themselves", summary, skipped, string.Empty)
                : new AnalystVerdict(AiReportStatus.Invalid, $"{symbol}: not read", summary, skipped, string.Join("; ", problems));
        }

        var metrics = new JsonArray();
        var allowed = new FactNumbers().AddFrom(sourceText);
        int withChange = 0;
        foreach (var m in (record["metrics"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            string name = AiJson.Str(m, "metric")?.ToLowerInvariant() ?? string.Empty;
            if (!Metrics.Contains(name)) problems.Add($"metric \"{name}\" is not one of the listed metrics");
            decimal? value = FactNumbers.Of(m["value"]);
            string? quote = AiJson.Str(m, "quote");
            decimal? previous = FactNumbers.Of(m["previous"]);
            string? previousQuote = AiJson.Str(m, "previousQuote");
            decimal? yoy = FactNumbers.Of(m["yoyPct"]);

            if (value is null) problems.Add($"{name}: no value");
            if (!Quoted(quote, haystack)) problems.Add($"{name}: the quote \"{Clip(quote, 60)}\" is not in the filing's text");
            else if (value is decimal v && !new FactNumbers().AddFrom(quote).Allows(v)) problems.Add($"{name}: {v} is not written in its quote");

            if (previous is decimal pv)
            {
                string? pq = string.IsNullOrWhiteSpace(previousQuote) ? quote : previousQuote;
                if (!Quoted(pq, haystack)) problems.Add($"{name}: the year-earlier quote \"{Clip(pq, 60)}\" is not in the filing's text");
                else if (!new FactNumbers().AddFrom(pq).Allows(pv)) problems.Add($"{name}: the year-earlier {pv} is not written in its quote");
            }

            // The change code finds from the two figures, as a check on the model's and as one it may say in words.
            decimal? computed = value is decimal cv && previous is decimal cp && cp != 0
                ? Math.Round((cv - cp) / Math.Abs(cp) * 100, 1, MidpointRounding.AwayFromZero)
                : null;
            if (computed is decimal c) allowed.Add(c, 1);
            if (yoy is decimal y && !textNumbers.Allows(y) && (computed is not decimal k || Math.Abs(Math.Abs(y) - Math.Abs(k)) > YoyTolerance))
            {
                problems.Add($"{name}: a year-on-year change of {y}% is neither written in the filing nor what its two figures give");
            }

            if (yoy is not null || computed is not null) withChange++;
            metrics.Add(new JsonObject
            {
                ["metric"] = name,
                ["value"] = value,
                ["unit"] = AiJson.Str(m, "unit"),
                ["quote"] = quote,
                ["previous"] = previous,
                ["previousQuote"] = previousQuote,
                ["yoyPct"] = yoy,
                ["yoyComputed"] = computed,
            });
        }

        if (!Surprises.Contains(surprise)) problems.Add($"surprise \"{surprise}\" is not positive, negative, inline or unclear");
        else if (surprise != "unclear" && withChange == 0) problems.Add($"a {surprise} surprise needs a year-on-year change behind it, and none was given");
        foreach (var n in allowed.Unsupported(summary)) problems.Add($"the summary states {n}, which is not in the filing");

        var data = new JsonObject
        {
            ["results"] = true,
            ["period"] = period,
            ["metrics"] = metrics,
            ["surprise"] = surprise,
            ["summary"] = summary,
        };

        var lead = metrics.OfType<JsonObject>().FirstOrDefault(x => x["yoyPct"] is not null || x["yoyComputed"] is not null) ?? metrics.OfType<JsonObject>().FirstOrDefault();
        string title = $"{symbol} results{(period.Length > 0 ? $" ({Clip(period, 40)})" : string.Empty)}: {(surprise.Length > 0 ? surprise : "?")}" +
                       (lead is null ? string.Empty : $", {MetricLine(lead)}");
        var body = new StringBuilder($"**Surprise:** {(surprise.Length > 0 ? surprise : "?")}{(period.Length > 0 ? $" · {period}" : string.Empty)}\n");
        if (summary.Length > 0) body.Append('\n').Append(summary).Append('\n');
        foreach (var x in metrics.OfType<JsonObject>()) body.Append($"\n- {MetricLine(x)} — \"{AiJson.Str(x, "quote")}\"");
        if (metrics.Count == 0) body.Append("\n_No numbers in the filing's text._");

        return problems.Count == 0
            ? new AnalystVerdict(AiReportStatus.Ok, title, body.ToString().TrimEnd(), data, string.Empty)
            : new AnalystVerdict(AiReportStatus.Invalid, title, body.ToString().TrimEnd(), data, string.Join("; ", problems));
    }

    /// <summary>"net profit 1,234.5 crore, +12.2% YoY".</summary>
    private static string MetricLine(JsonObject m)
    {
        string value = FactNumbers.Of(m["value"])?.ToString("#,##0.##", CultureInfo.InvariantCulture) ?? "?";
        decimal? change = FactNumbers.Of(m["yoyPct"]) ?? FactNumbers.Of(m["yoyComputed"]);
        string yoy = change is decimal c ? $", {(c > 0 ? "+" : string.Empty)}{c.ToString("0.#", CultureInfo.InvariantCulture)}% YoY" : string.Empty;
        return $"{AiJson.Str(m, "metric")} {value} {AiJson.Str(m, "unit")}".TrimEnd() + yoy;
    }

    private static bool Quoted(string? quote, string haystack) =>
        !string.IsNullOrWhiteSpace(quote) && haystack.Contains(FactNumbers.Normalise(quote), StringComparison.Ordinal);

    private static string Clip(string? text, int max)
    {
        string t = (text ?? string.Empty).Trim();
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
}
