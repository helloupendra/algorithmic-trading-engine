using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;
using Microsoft.EntityFrameworkCore;
using static AlgoTrading.Api.Services.AiTools.AiToolFormat;

namespace AlgoTrading.Api.Services.AiTools;

/// <summary><c>get_quotes</c>: the Market pulse's indices, large caps and commodities with their day change, and India VIX.</summary>
public sealed class QuotesTool(IMarketPulseService pulse, OptionChainService chains, TimeProvider? time = null) : IAiTool
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => AiToolNames.Quotes;

    public string Description =>
        "Latest prices with the day's change: the indices (NIFTY 50, NIFTY BANK, SENSEX...), large caps and MCX " +
        "commodities from the Market pulse, and India VIX. Each price has the time it was last updated: an old time " +
        "means the market is closed or the feed is not updating.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("group", AiToolSchema.OneOf("Which group. Default indices.", "indices", "largecaps", "commodities", "all"), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        string wanted = args.String("group", 12)?.ToLowerInvariant() ?? "indices";
        string? key = wanted switch
        {
            "indices" => "index",
            "largecaps" => "equity",
            "commodities" => "commodity",
            "all" => null,
            _ => throw new AiToolArgumentException("group is indices, largecaps, commodities or all."),
        };

        var response = await pulse.GetAsync(cancellationToken);
        var items = response.Groups
            .Where(g => key is null || g.Key == key)
            .SelectMany(g => g.Items.Select(i => new
            {
                group = g.Title,
                name = i.Name,
                contract = i.Contract,
                last = i.LastTradedPrice,
                previousClose = i.PreviousClose,
                change = i.Change,
                changePercent = i.ChangePercent,
                high = i.High,
                low = i.Low,
                updated = Ist(i.UpdatedUtc),
            }))
            .ToList();

        object? vix = null;
        if (key is null or "index")
        {
            // VIX is not in the pulse; the chain header carries it, as on the Desk.
            try
            {
                var nifty = await chains.GetViewAsync("NIFTY", null, null, cancellationToken);
                if (nifty.Header?.Vix is { } v)
                {
                    vix = new { name = "INDIA VIX", last = v.LastPrice, change = v.Change, changePercent = v.ChangePercent, updated = Ist(v.AsOfUtc) };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // No NIFTY chain to read VIX from: the quotes still stand.
            }
        }

        return new AiToolOutput(new { quotes = items, indiaVix = vix }, response.LatestQuoteUtc ?? _time.GetUtcNow().UtcDateTime,
            items.Count, $"{items.Count} quotes");
    }
}

/// <summary><c>get_option_chain_summary</c>: an index chain's levels, from the header the Desk and the Option chain page show.</summary>
public sealed class OptionChainTool(OptionChainService chains) : IAiTool
{
    public string Name => AiToolNames.OptionChain;

    public string Description =>
        "An index option chain's summary for its nearest (or a given) expiry: spot, future and VIX, ATM strike and IV, " +
        "put-call ratio (and of the day's OI change), max pain, the put wall (support) and call wall (resistance) " +
        "with their open interest, total call and put OI and change, days to expiry, lot size, and whether it is " +
        "live or the last snapshot.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("underlying", AiToolSchema.Text("NIFTY, BANKNIFTY, FINNIFTY, MIDCPNIFTY, SENSEX, BANKEX, or an MCX commodity such as CRUDEOIL."), true),
        ("expiry", AiToolSchema.Text("Expiry as yyyy-MM-dd. Default the nearest."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        string underlying = (args.String("underlying", 20) ?? throw new AiToolArgumentException("underlying is required, e.g. NIFTY.")).ToUpperInvariant();
        DateOnly? expiry = args.Date("expiry", DateOnly.MinValue) is DateOnly e && e != DateOnly.MinValue ? e : null;

        var chain = await chains.GetViewAsync(underlying, expiry, null, cancellationToken);
        var h = chain.Header;
        if (h is null || chain.Strikes.Count == 0)
        {
            return new AiToolOutput(new { underlying, found = false, note = "No chain recorded for this underlying and expiry." }, null, 0, "no chain");
        }

        var data = new
        {
            underlying = chain.Underlying,
            expiry = chain.ExpiryDate.ToString("yyyy-MM-dd"),
            daysToExpiry = h.DaysToExpiry,
            mode = h.Mode,
            marketOpen = h.MarketOpen,
            spot = h.Spot is { } s ? new { last = s.LastPrice, change = s.Change, changePercent = s.ChangePercent, isFuture = h.SpotIsFuture } : null,
            future = h.Future is { } f ? new { last = f.LastPrice, change = f.Change, expiry = f.ExpiryDate?.ToString("yyyy-MM-dd"), premiumOverSpot = f.PremiumOverSpot } : null,
            vix = h.Vix is { } v ? new { last = v.LastPrice, change = v.Change } : null,
            atmStrike = h.AtTheMoneyStrike,
            atmIv = h.AtTheMoneyIv,
            putCallRatio = h.PutCallRatio,
            putCallRatioOfChange = h.PutCallRatioOfChange,
            maxPain = h.MaxPainStrike,
            putWall = h.SupportStrike is null ? null : new { strike = h.SupportStrike, openInterest = h.SupportOpenInterest },
            callWall = h.ResistanceStrike is null ? null : new { strike = h.ResistanceStrike, openInterest = h.ResistanceOpenInterest },
            totalCallOi = h.TotalCallOpenInterest,
            totalPutOi = h.TotalPutOpenInterest,
            callOiChange = h.TotalCallOpenInterestChange,
            putOiChange = h.TotalPutOpenInterestChange,
            lotSize = h.LotSize,
            openInterestUnavailable = chain.OpenInterestUnavailable ? true : (bool?)null,
            snapshotAt = Ist(h.SnapshotCapturedUtc),
            liveAt = Ist(h.LiveOverlayUtc),
        };

        return new AiToolOutput(data, h.LiveOverlayUtc ?? h.SnapshotCapturedUtc ?? chain.AsOfUtc, chain.Strikes.Count,
            $"{chain.Underlying} {chain.ExpiryDate:dd MMM}: PCR {h.PutCallRatio:0.00}, max pain {h.MaxPainStrike:0}");
    }
}

/// <summary><c>get_news</c>: recorded headlines and exchange filings, with FinBERT's sentiment where it has scored them.</summary>
public sealed class NewsTool(MarketIntelligenceQueries queries, TimeProvider? time = null) : IAiTool
{
    private const int MaxHeadlines = 25;
    private const int MaxFilings = 15;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => AiToolNames.News;

    public string Description =>
        "Recorded market news headlines, and exchange filings for a stock, from the last hours: time, source, title, " +
        "a short summary, FinBERT sentiment (-1 to 1, missing when not scored yet: scoring pauses during the session) " +
        "and the symbols tagged. Filter by a word or an NSE symbol.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("query", AiToolSchema.Text("A word or NSE symbol to match, e.g. RELIANCE, RBI, crude."), false),
        ("hours", AiToolSchema.Integer("How far back, 1 to 168. Default 24."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        string? query = args.String("query", 40);
        int hours = args.Int("hours", 1, 168) ?? 24;
        var now = _time.GetUtcNow().UtcDateTime;
        var from = now.AddHours(-hours);

        var news = await queries.NewsAsync(from, now, null, query, 0, MaxHeadlines, cancellationToken);
        var filings = query is { Length: > 0 } symbol && Regex.IsMatch(symbol, "^[A-Za-z0-9&-]{1,20}$")
            ? await queries.AnnouncementsAsync(symbol.ToUpperInvariant(), from, now, 0, MaxFilings, cancellationToken)
            : null;

        var data = new
        {
            from = Ist(from),
            headlines = news.Items.Select(n => new
            {
                at = Ist(n.PublishedUtc ?? n.FirstSeenUtc),
                source = n.Source,
                category = n.Category,
                title = Text(n.Title, 300),
                summary = Text(n.Summary, 300),
                sentiment = n.Sentiment,
                symbols = n.Symbols,
            }).ToList(),
            headlinesTotal = news.Total,
            filings = filings?.Items.Select(a => new
            {
                at = Ist(a.AnnouncedUtc),
                exchange = a.Exchange,
                symbol = a.Symbol,
                subject = Text(a.Subject, 200),
                details = Text(a.Details, 300),
                sentiment = a.Sentiment,
            }).ToList(),
        };

        int rows = news.Items.Count + (filings?.Items.Count ?? 0);
        return new AiToolOutput(data, now, rows, $"{news.Items.Count} headlines" + (filings is null ? string.Empty : $", {filings.Items.Count} filings"));
    }
}

/// <summary><c>get_incidents</c>: Sentinel's incidents, text masked as on the Incidents page.</summary>
public sealed class IncidentsTool(TradingDbContext db, TimeProvider? time = null) : IAiTool
{
    private const int MaxIncidents = 40;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => AiToolNames.Incidents;

    public string Description =>
        "Sentinel's incidents: open and acknowledged ones by default, or every incident seen in the last days. Each has " +
        "its agent and rule, severity, status, title, summary, a few lines of evidence, the suggestion, how many times it " +
        "fired, first and last seen, and how it was resolved.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("which", AiToolSchema.OneOf("live = open or acknowledged (default); recent = every incident seen in the last days.", "live", "recent"), false),
        ("days", AiToolSchema.Integer("For recent: how many days back, 1 to 14. Default 2."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        string which = args.String("which", 10)?.ToLowerInvariant() ?? "live";
        int days = args.Int("days", 1, 14) ?? 2;
        var now = _time.GetUtcNow().UtcDateTime;

        var query = db.Incidents.AsNoTracking();
        query = which switch
        {
            "live" => query.Where(i => IncidentStatus.Live.Contains(i.Status)),
            "recent" => query.Where(i => i.LastSeenUtc >= now.AddDays(-days)),
            _ => throw new AiToolArgumentException("which is live or recent."),
        };

        var rows = await query.OrderByDescending(i => i.LastSeenUtc).Take(MaxIncidents).ToListAsync(cancellationToken);
        var data = rows.Select(i => new
        {
            id = i.Id,
            agent = i.Agent,
            rule = i.Rule,
            severity = i.Severity,
            status = i.Status,
            title = Text(i.Title, 200),
            summary = Text(i.Summary, 400),
            evidence = Evidence(i.EvidenceJson),
            suggestion = Text(i.Suggestion, 300),
            occurrences = i.Occurrences,
            firstSeen = Ist(i.FirstSeenUtc),
            lastSeen = Ist(i.LastSeenUtc),
            resolved = Ist(i.ResolvedUtc),
            rootCause = Text(i.RootCause, 300),
            resolution = Text(i.Resolution, 300),
        }).ToList();

        return new AiToolOutput(new { which, incidents = data }, now, data.Count, $"{data.Count} incidents ({which})");
    }

    /// <summary>The first few evidence lines, masked: they quote log lines, which can hold anything.</summary>
    private static List<string>? Evidence(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            if (JsonNode.Parse(json) is not JsonArray items) return [Text(json, 300)!];
            return items.Take(4)
                .Select(e => e is JsonValue v && v.TryGetValue(out string? s) ? s : e?.ToJsonString())
                .Select(s => Text(s, 300))
                .Where(s => s is not null)
                .Select(s => s!)
                .ToList();
        }
        catch (JsonException)
        {
            return [Text(json, 300)!];
        }
    }
}

/// <summary><c>get_latest_checkup</c>: Sentinel's newest finished desk checkup, every item with what to do.</summary>
public sealed class CheckupTool(TradingDbContext db) : IAiTool
{
    public string Name => AiToolNames.Checkup;

    public string Description =>
        "Sentinel's newest finished desk checkup (before the open, after the close, end of day, weekly or on request): " +
        "its verdict and headline, and every item with its state (ok, warn, fail, info, skip), what was found and what " +
        "to do.";

    public JsonObject Parameters => AiToolSchema.Object();

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        string[] finished = DeskCheckupStatus.Finished.ToArray();
        var row = await db.DeskCheckups.AsNoTracking()
            .Where(x => finished.Contains(x.Status))
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null) return new AiToolOutput(new { found = false, note = "No checkup has finished yet." }, null, 0, "no checkup");

        // The page's own mapper, which masks every text; the host and who asked stay behind.
        var d = CheckupsController.ToDetail(row);
        var data = new
        {
            slot = d.Slot,
            status = d.Status,
            verdict = d.Verdict,
            headline = Text(d.Headline, 300),
            completed = Ist(d.CompletedUtc),
            counts = d.Counts,
            items = d.Items.Select(i => new { area = i.Area, title = Text(i.Title, 200), state = i.State, detail = Text(i.Detail, 400), action = Text(i.Action, 300) }).ToList(),
            error = Text(d.Error, 300),
        };

        return new AiToolOutput(data, d.CompletedUtc, d.Items.Count, $"{d.Slot} checkup: {d.Verdict}");
    }
}

/// <summary><c>get_forecasts</c>: the Analysis module's forecasts for a day, with their scores once the close has come.</summary>
public sealed class ForecastsTool(TradingDbContext db, TimeProvider? time = null) : IAiTool
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => AiToolNames.Forecasts;

    public string Description =>
        "The desk's statistical forecasts for one IST session (default today): per index and target (range, trend, " +
        "direction) the model, what it predicted and its baseline, and after the close the outcome and scores. These are " +
        "the desk's own models, not advice.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("date", AiToolSchema.Text("IST session day as yyyy-MM-dd, or 'today' / 'yesterday'. Default today."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var today = IstTime.DateOf(now);
        var date = args.Date("date", today) ?? today;

        var rows = await db.Forecasts.AsNoTracking()
            .Where(f => f.SessionDate == date)
            .OrderBy(f => f.Underlying).ThenBy(f => f.Target).ThenBy(f => f.ModelKey)
            .Take(60)
            .ToListAsync(cancellationToken);

        var data = rows.Select(ForecastsController.ToView).Select(f => new
        {
            underlying = f.Underlying,
            target = f.Target,
            model = f.ModelKey,
            issued = Ist(f.IssuedUtc),
            prediction = f.Prediction,
            baseline = f.Baseline,
            outcome = f.Outcome,
            scores = f.Scores,
            scored = Ist(f.ScoredUtc),
        }).ToList();

        return new AiToolOutput(new { date = date.ToString("yyyy-MM-dd"), forecasts = data },
            rows.Count == 0 ? null : rows.Max(f => f.ScoredUtc ?? f.IssuedUtc), data.Count, $"{data.Count} forecasts for {date:dd MMM}");
    }
}

/// <summary><c>get_strategy_spec</c>: a strategy's written specification, as its Library page shows it; or the list of strategies.</summary>
public sealed class StrategySpecTool(StrategyCatalogService catalog, IWebHostEnvironment env) : IAiTool
{
    private const int MaxChars = 12_000;
    private static readonly Regex SpecName = new("^[A-Za-z0-9]+$", RegexOptions.CultureInvariant);

    public string Name => AiToolNames.StrategySpec;

    public string Description =>
        "A strategy's written specification (its rules for entry, exit, stops, sizing and the facts it was verified " +
        "against), by the strategy name get_runs shows. Without a name, the list of strategies with one line each.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("strategy", AiToolSchema.Text("The strategy's name, as get_runs shows it."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        string? name = args.String("strategy", 80);
        if (name is null)
        {
            var all = await catalog.GetAllAsync(cancellationToken);
            var list = all.Select(s => new { name = s.Name, category = s.Category, description = Text(s.Description, 160) }).ToList();
            return new AiToolOutput(new { strategies = list }, null, list.Count, $"{list.Count} strategies");
        }

        var entry = await catalog.FindByNameAsync(name, cancellationToken)
            ?? throw new AiToolArgumentException($"No strategy named {name}; call get_strategy_spec without a name for the list.");

        string? markdown = null;
        if (SpecName.IsMatch(entry.Name))
        {
            string folder = Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "..", "docs", "strategies"));
            string file = Path.GetFullPath(Path.Combine(folder, entry.Name + ".md"));
            if (file.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(file))
            {
                // The comments hold the author's verification SQL and reviewer notes, as on the spec page.
                markdown = Regex.Replace(await File.ReadAllTextAsync(file, cancellationToken), @"<!--[\s\S]*?-->", string.Empty).Trim();
            }
        }

        var data = new
        {
            name = entry.Name,
            category = entry.Category,
            description = entry.Description,
            legs = entry.LegsSummary,
            underlyings = entry.SupportedUnderlyings,
            spec = markdown is null ? null : markdown.Length <= MaxChars ? markdown : markdown[..MaxChars] + "\n[cut]",
            note = markdown is null ? "No written spec for this strategy." : null,
        };

        return new AiToolOutput(data, null, 1, markdown is null ? $"{entry.Name}: no spec" : $"{entry.Name} spec, {markdown.Length:N0} characters");
    }
}
