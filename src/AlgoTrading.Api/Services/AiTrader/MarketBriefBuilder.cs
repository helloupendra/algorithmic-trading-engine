using System.Globalization;
using System.Text;
using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Contracts.OptionChain;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Patterns;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using AlgoTrading.Infrastructure.Services.MarketFactors;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.AiTrader;

/// <summary>
/// The market brief the AI Trader reads: the facts code computed from the desk's own records, so the model
/// reads numbers, not raw data. It is built as of a moment: now, or a market replay's clock. Nothing later
/// than that moment can reach it.
/// </summary>
/// <remarks>
/// Each section is built on its own. A source that fails or has nothing says so in one line, and the rest
/// of the brief stands: the model must know what it was not told.
/// </remarks>
public sealed class MarketBriefBuilder(
    TradingDbContext db,
    ILiveDataService live,
    OptionChainService chains,
    MarketIntelligenceQueries intel,
    MarketFactorsQueries factors,
    IMarketSessionService sessions,
    ILogger<MarketBriefBuilder> logger,
    IMarketReplayBook? replayBook = null,
    SimilarMomentsSection? similar = null) : IAiTraderBriefs
{
    /// <summary>The indices it trades, with their spot symbols and exchanges.</summary>
    public static readonly IReadOnlyList<(string Name, string Spot, string Exchange)> Indices =
    [
        ("NIFTY", "NSE:NIFTY50-INDEX", "NSE"),
        ("BANKNIFTY", "NSE:NIFTYBANK-INDEX", "NSE"),
        ("SENSEX", "BSE:SENSEX-INDEX", "BSE"),
    ];

    public const string VixSymbol = "NSE:INDIAVIX-INDEX";

    /// <summary>Minutes read per index: a little over two sessions, so EMA 50 on 5-minute bars is warm by the open.</summary>
    private const int MinutesRead = 1000;

    /// <summary>How far back the news goes.</summary>
    private static readonly TimeSpan NewsWindow = TimeSpan.FromHours(1);

    private static readonly CultureInfo India = CultureInfo.GetCultureInfo("en-IN");

    /// <summary>The brief as of <paramref name="asOfUtc"/>. <paramref name="replay"/> reads a market replay's prices and the recorded chain, never the live ones.</summary>
    public async Task<MarketBrief> BuildAsync(DateTime asOfUtc, bool replay, CancellationToken cancellationToken)
    {
        var asOf = DateTime.SpecifyKind(asOfUtc, DateTimeKind.Utc);
        var day = IstTime.DateOf(asOf);
        var session = sessions.GetSessionInfo(asOf, "NSE", "FO");
        var text = new StringBuilder();
        var chainsByUnderlying = new Dictionary<string, OptionChainResponse>(StringComparer.OrdinalIgnoreCase);

        var ist = IstTime.ToIst(asOf);
        text.Append(CultureInfo.InvariantCulture, $"MARKET BRIEF — {ist:ddd d MMM yyyy, HH:mm} IST{(replay ? " (a replay of that day)" : string.Empty)}\n");
        if (session.IsTradingDay)
        {
            int since = (int)Math.Max(0, (asOf - session.SessionOpenUtc).TotalMinutes);
            int left = (int)Math.Max(0, (session.SessionCloseUtc - asOf).TotalMinutes);
            text.Append(CultureInfo.InvariantCulture, $"NSE session 09:15–15:30: {since} min since the open, {left} min to the close.\n");
        }
        else
        {
            text.Append("No NSE session today.\n");
        }

        text.Append("\nINDICES (5-minute bars; EMA and ATR on them)\n");
        foreach (var (name, spot, exchange) in Indices)
        {
            text.Append(await Section($"{name} index", () => IndexLineAsync(name, spot, exchange, asOf, replay, cancellationToken))).Append('\n');
        }

        text.Append(await Section("India VIX", () => VixLineAsync(asOf, replay, cancellationToken))).Append('\n');

        if (similar is not null)
        {
            text.Append('\n').Append(SimilarMomentsSection.Header).Append('\n')
                .Append(await Section("similar moments", () => similar.BuildAsync(asOf, cancellationToken))).Append('\n');
        }

        text.Append("\nOPTION CHAINS (nearest expiry)\n");
        foreach (var (name, _, _) in Indices)
        {
            text.Append(await Section($"{name} chain", async () =>
            {
                var (line, chain) = await ChainLineAsync(name, asOf, replay, cancellationToken);
                if (chain is not null) chainsByUnderlying[name] = chain;
                return line;
            })).Append('\n');
        }

        text.Append("\nTODAY'S FORECASTS (issued before the open)\n").Append(await Section("forecasts", () => ForecastsAsync(day, asOf, cancellationToken))).Append('\n');
        text.Append("\nDAY CONTEXT\n").Append(await Section("context", () => ContextAsync(day, asOf, cancellationToken))).Append('\n');
        text.Append("\nNEWS (the last hour, as the News Analyst read it)\n").Append(await Section("news", () => NewsAsync(asOf, cancellationToken))).Append('\n');

        return new MarketBrief(asOf, replay, text.ToString(), chainsByUnderlying);
    }

    private async Task<string> Section(string what, Func<Task<string>> build)
    {
        try
        {
            return await build();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "AI Trader brief: the {Section} section could not be built", what);
            return $"{what}: not available just now.";
        }
    }

    // ---------- indices ----------

    private async Task<IReadOnlyList<LiveBarResponse>> MinutesAsync(string symbol, DateTime asOf, bool replay, CancellationToken cancellationToken)
    {
        var current = replay && replayBook?.Day is not null ? replayBook.CurrentMinute(symbol) : null;
        var newestFirst = await live.GetBarsUntilAsync(symbol, "1m", MinutesRead, asOf, current, cancellationToken);
        return newestFirst.OrderBy(b => b.BarStartUtc).ToList();
    }

    private async Task<string> IndexLineAsync(string name, string spot, string exchange, DateTime asOf, bool replay, CancellationToken cancellationToken)
    {
        var minutes = await MinutesAsync(spot, asOf, replay, cancellationToken);
        var info = sessions.GetSessionInfo(asOf, exchange, "FO");
        var today = minutes.Where(b => b.BarStartUtc >= info.SessionOpenUtc && b.BarStartUtc < info.SessionCloseUtc).ToList();
        if (today.Count == 0) return $"{name}: no bars recorded yet today.";

        var dayStart = IstTime.StartOfDayUtc(IstTime.DateOf(asOf));
        decimal? previousClose = minutes.LastOrDefault(b => b.BarStartUtc < dayStart)?.Close;
        decimal last = (replay ? replayBook?.Quote(spot)?.LastTradedPrice : null) ?? today[^1].Close;
        decimal open = today[0].Open, high = today.Max(b => b.High), low = today.Min(b => b.Low);

        var fives = FiveMinute(minutes);
        var closes = fives.Select(b => (double)b.Close).ToList();
        double? ema20 = LastOf(IndicatorMath.Ema(closes, 20));
        double? ema50 = LastOf(IndicatorMath.Ema(closes, 50));
        double? atr = Atr(fives, 14);
        decimal? halfHourAgo = today.LastOrDefault(b => b.BarStartUtc <= today[^1].BarStartUtc.AddMinutes(-30))?.Close;

        var line = new StringBuilder();
        line.Append(CultureInfo.InvariantCulture, $"{name} {Px(last)}");
        if (previousClose is decimal pc && pc > 0) line.Append(CultureInfo.InvariantCulture, $" ({Pct((last - pc) / pc)} vs the previous close {Px(pc)})");
        line.Append(CultureInfo.InvariantCulture, $" | open {Px(open)} high {Px(high)} low {Px(low)}, range {Px(high - low)}");
        if (atr is double a) line.Append(CultureInfo.InvariantCulture, $" | ATR(14) {a:0.#}");
        if (ema20 is double e20 && ema50 is double e50)
        {
            string where = (double)last > Math.Max(e20, e50) ? "above both" : (double)last < Math.Min(e20, e50) ? "below both" : "between them";
            line.Append(CultureInfo.InvariantCulture, $" | EMA20 {e20:0.#}, EMA50 {e50:0.#}: price {where}, EMA20 {(e20 > e50 ? "above" : "below")} EMA50");
        }

        if (halfHourAgo is decimal h && h > 0) line.Append(CultureInfo.InvariantCulture, $" | last 30 min {Pct((last - h) / h)}");
        line.Append(CultureInfo.InvariantCulture, $" | from the day's high {Px(last - high)}, from the low +{Px(last - low)}");
        return line.ToString();
    }

    private async Task<string> VixLineAsync(DateTime asOf, bool replay, CancellationToken cancellationToken)
    {
        var minutes = await MinutesAsync(VixSymbol, asOf, replay, cancellationToken);
        var dayStart = IstTime.StartOfDayUtc(IstTime.DateOf(asOf));
        var today = minutes.Where(b => b.BarStartUtc >= dayStart).ToList();
        if (today.Count == 0) return "India VIX: no bars recorded yet today.";
        decimal last = (replay ? replayBook?.Quote(VixSymbol)?.LastTradedPrice : null) ?? today[^1].Close;
        decimal? previous = minutes.LastOrDefault(b => b.BarStartUtc < dayStart)?.Close;
        return previous is decimal p && p > 0
            ? string.Create(CultureInfo.InvariantCulture, $"India VIX {last:0.00} ({Pct((last - p) / p)} vs {p:0.00}); day range {today.Min(b => b.Low):0.00}–{today.Max(b => b.High):0.00}")
            : string.Create(CultureInfo.InvariantCulture, $"India VIX {last:0.00}");
    }

    // ---------- option chains ----------

    private async Task<(string Line, OptionChainResponse? Chain)> ChainLineAsync(string underlying, DateTime asOf, bool replay, CancellationToken cancellationToken)
    {
        var view = await chains.GetViewAsync(underlying, null, replay ? asOf : null, cancellationToken);
        if (view.Strikes.Count == 0) return ($"{underlying}: no option chain recorded.", null);

        // The newest capture can be an earlier day's (before the day's first capture, or with the recorder down):
        // its premiums and OI are that day's, and after an expiry its contracts are gone. It is not offered.
        if (IstTime.DateOf(view.AsOfUtc) != IstTime.DateOf(asOf))
        {
            return ($"{underlying}: no chain recorded yet today (the last capture is from {IstTime.ToIst(view.AsOfUtc).ToString("d MMM HH:mm", CultureInfo.InvariantCulture)}).", null);
        }

        var header = view.Header;
        var line = new StringBuilder();
        line.Append(CultureInfo.InvariantCulture, $"{underlying} expiry {view.ExpiryDate:d MMM}");
        if (header?.DaysToExpiry is int dte) line.Append(CultureInfo.InvariantCulture, $" ({dte} day{(dte == 1 ? string.Empty : "s")} to go)");
        if (header?.LotSize is int lot) line.Append(CultureInfo.InvariantCulture, $", lot {lot}");

        var atmIndex = view.Strikes.FindIndex(s => s.IsAtTheMoney);
        if (atmIndex >= 0)
        {
            line.Append(" | premiums (bid/ask, last):");
            for (int i = Math.Max(0, atmIndex - 1); i <= Math.Min(view.Strikes.Count - 1, atmIndex + 1); i++)
            {
                var s = view.Strikes[i];
                string tag = i == atmIndex ? "ATM" : i < atmIndex ? "ATM-1" : "ATM+1";
                line.Append(CultureInfo.InvariantCulture, $" {tag} {s.StrikePrice:0} CE {Leg(s.Call)} PE {Leg(s.Put)};");
            }
        }

        if (header is not null)
        {
            line.Append(CultureInfo.InvariantCulture, $" | PCR {Num(header.PutCallRatio)}, of today's OI change {Num(header.PutCallRatioOfChange)}");
            line.Append(CultureInfo.InvariantCulture, $" | max pain {Num(header.MaxPainStrike, "0")}");
            line.Append(CultureInfo.InvariantCulture, $" | call wall {Num(header.ResistanceStrike, "0")} (OI {Lakh(header.ResistanceOpenInterest)}), put wall {Num(header.SupportStrike, "0")} (OI {Lakh(header.SupportOpenInterest)})");
            line.Append(CultureInfo.InvariantCulture, $" | OI change on the day: calls {Signed(Lakh(header.TotalCallOpenInterestChange))}, puts {Signed(Lakh(header.TotalPutOpenInterestChange))}");
            if (header.AtTheMoneyIv is decimal iv) line.Append(CultureInfo.InvariantCulture, $" | ATM IV {iv:0.0}");
        }

        var trend = await chains.GetTrendAsync(underlying, view.ExpiryDate, replay ? asOf : null, 400, cancellationToken);
        var points = trend.Points.Where(p => p.CapturedUtc <= asOf && IstTime.DateOf(p.CapturedUtc) == IstTime.DateOf(asOf)).ToList();
        if (points.Count >= 2)
        {
            var first = points[0];
            var latest = points[^1];
            line.Append(CultureInfo.InvariantCulture,
                $" | since {IstTime.ToIst(first.CapturedUtc):HH:mm}: call OI {Signed(Lakh(latest.CallOpenInterest - first.CallOpenInterest))}, put OI {Signed(Lakh(latest.PutOpenInterest - first.PutOpenInterest))}");
        }

        return (line.ToString(), view);
    }

    private static string Leg(OptionChainLegResponse? leg)
    {
        if (leg is null) return "—";
        string book = leg.BidPrice is decimal b && leg.AskPrice is decimal a ? string.Create(CultureInfo.InvariantCulture, $"{b:0.##}/{a:0.##}") : "no book";
        return leg.LastTradedPrice is decimal ltp ? string.Create(CultureInfo.InvariantCulture, $"{book}, {ltp:0.##}") : book;
    }

    // ---------- the day's context ----------

    private async Task<string> ForecastsAsync(DateOnly day, DateTime asOf, CancellationToken cancellationToken)
    {
        var rows = await db.Forecasts.AsNoTracking()
            .Where(f => f.SessionDate == day && f.IssuedUtc <= asOf)
            .OrderBy(f => f.Underlying).ThenBy(f => f.Target)
            .Select(f => new { f.Underlying, f.Target, f.ModelKey, f.PredictionJson })
            .Take(12)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return "None issued for today.";
        // The prediction only: the outcome and its scores are hindsight, written after the close.
        return string.Join('\n', rows.Select(r => $"- {r.Underlying} {r.Target} ({r.ModelKey}): {Prediction(r.PredictionJson)}"));
    }

    /// <summary>
    /// A forecast's prediction in words. A range forecast (the session's high − low, as % of the previous close)
    /// reads as its median and 80% band, in percent and points, with the chances of a quiet, normal or wild day,
    /// and says it has no direction: the model once read it as room to rise. Any other shape reads as its JSON,
    /// cut short.
    /// </summary>
    public static string Prediction(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("median", out var median) && median.ValueKind == JsonValueKind.Number
                && root.TryGetProperty("low80", out var low) && root.TryGetProperty("high80", out var high))
            {
                var text = string.Create(CultureInfo.InvariantCulture, $"the session's high−low range, median {median.GetDouble():0.00}%");
                if (root.TryGetProperty("points", out var points) && points.TryGetProperty("median", out var pm))
                {
                    text += string.Create(CultureInfo.InvariantCulture, $" (≈{pm.GetDouble():0} pts)");
                }

                text += string.Create(CultureInfo.InvariantCulture, $", 80% between {low.GetDouble():0.00}% and {high.GetDouble():0.00}%");
                if (root.TryGetProperty("buckets", out var buckets) && buckets.ValueKind == JsonValueKind.Object)
                {
                    text += "; " + string.Join(", ", buckets.EnumerateObject()
                        .Where(b => b.Value.ValueKind == JsonValueKind.Number)
                        .Select(b => string.Create(CultureInfo.InvariantCulture, $"{b.Name} {b.Value.GetDouble() * 100:0}%")));
                }

                return text + " (a size, not a direction)";
            }

            return Cut(JsonSerializer.Serialize(root), 220);
        }
        catch (JsonException)
        {
            return Cut(json, 220);
        }
    }

    private async Task<string> ContextAsync(DateOnly day, DateTime asOf, CancellationToken cancellationToken)
    {
        var lines = new List<string>();

        var openUtc = IstTime.FromIst(day.ToDateTime(new TimeOnly(9, 15)));
        var gift = (await intel.SnapshotsAsync("GIFTNIFTY", day, cancellationToken))
            .Where(s => s.AsOfUtc is DateTime at && at <= (asOf < openUtc ? asOf : openUtc))
            .OrderBy(s => s.AsOfUtc)
            .LastOrDefault();
        lines.Add(gift is null
            ? "GIFT Nifty before the open: not recorded."
            : string.Create(CultureInfo.InvariantCulture, $"GIFT Nifty before the open: {gift.Price:0.#}{(gift.ChangePct is decimal c ? $" ({c:+0.00;-0.00}%)" : string.Empty)} at {IstTime.ToIst(gift.AsOfUtc!.Value):HH:mm}."));

        var flowDay = await db.MarketCashFlows.AsNoTracking().Where(f => f.Date < day).MaxAsync(f => (DateOnly?)f.Date, cancellationToken);
        if (flowDay is DateOnly fd)
        {
            var flows = await db.MarketCashFlows.AsNoTracking().Where(f => f.Date == fd).Select(f => new { f.Category, f.NetValueCrore }).ToListAsync(cancellationToken);
            lines.Add($"Cash flows on {fd:d MMM}: " + string.Join(", ", flows.Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Category} net {f.NetValueCrore:+#,##0;-#,##0} cr"))) + ".");
        }
        else
        {
            lines.Add("FII/DII cash flows: not recorded.");
        }

        var events = (await factors.EventsAsync(day, day, cancellationToken)).Events
            .OrderByDescending(e => e.Importance).ThenBy(e => e.TimeIst).Take(6).ToList();
        lines.Add(events.Count == 0
            ? "Events today: none listed."
            : "Events today: " + string.Join("; ", events.Select(e => $"{(string.IsNullOrWhiteSpace(e.TimeIst) ? string.Empty : e.TimeIst + " ")}{e.Title} ({e.Category}, importance {e.Importance})")) + ".");
        return string.Join('\n', lines);
    }

    private async Task<string> NewsAsync(DateTime asOf, CancellationToken cancellationToken)
    {
        var from = asOf - NewsWindow;
        var reports = await db.AiReports.AsNoTracking()
            .Where(r => r.AgentKey == AiCatalog.NewsAnalyst && r.Status == AiReportStatus.Ok
                        && (r.SubjectType == AiReportSubject.News || r.SubjectType == AiReportSubject.Filing)
                        && r.CreatedUtc >= from && r.CreatedUtc <= asOf)
            .OrderByDescending(r => r.CreatedUtc)
            .Select(r => new { r.CreatedUtc, r.Title, r.DataJson })
            .Take(40)
            .ToListAsync(cancellationToken);

        // What can move an index first: policy, macro data, results and rating changes, and anything with a
        // direction. A routine filing ("other", neutral) is left out: the hour is full of them.
        var items = new List<(int Rank, DateTime At, string Line)>();
        foreach (var r in reports)
        {
            if (AiJson.Object(r.DataJson) is not { } data) continue;
            string ev = AiJson.Str(data, "event") ?? string.Empty;
            string direction = AiJson.Str(data, "direction") ?? "unclear";
            if (ev is "" or "none") continue;
            bool directional = direction is "positive" or "negative";
            if (ev == "other" && !directional) continue;
            int rank = ev is "policy" or "macro data" ? 0 : ev is "results" or "rating change" or "guidance" ? 1 : directional ? 2 : 3;
            var symbols = AiJson.Strings(data, "symbols");
            items.Add((rank, r.CreatedUtc, $"- {IstTime.ToIst(r.CreatedUtc):HH:mm} {ev}, {direction}"
                + (symbols.Count > 0 ? $" ({string.Join(", ", symbols.Take(4))})" : string.Empty) + $": {Cut(r.Title, 140)}"));
        }

        var lines = items.OrderBy(i => i.Rank).ThenByDescending(i => i.At).Take(10).Select(i => i.Line).ToList();
        return lines.Count == 0 ? "Nothing that could move an index in the last hour." : string.Join('\n', lines);
    }

    // ---------- arithmetic ----------

    /// <summary>1-minute bars rolled into 5-minute ones on the IST grid (09:15, 09:20, …), oldest first.</summary>
    public static List<LiveBarResponse> FiveMinute(IEnumerable<LiveBarResponse> minutesOldestFirst) =>
        minutesOldestFirst
            .GroupBy(b => new DateTime(b.BarStartUtc.Ticks - b.BarStartUtc.Ticks % TimeSpan.FromMinutes(5).Ticks, DateTimeKind.Utc))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var ordered = g.OrderBy(b => b.BarStartUtc).ToList();
                return new LiveBarResponse
                {
                    Symbol = ordered[0].Symbol, Resolution = "5m", BarStartUtc = g.Key, Open = ordered[0].Open,
                    High = ordered.Max(b => b.High), Low = ordered.Min(b => b.Low), Close = ordered[^1].Close,
                };
            })
            .ToList();

    /// <summary>The mean true range of the last <paramref name="period"/> bars, as the strategies' indicators compute it.</summary>
    public static double? Atr(IReadOnlyList<LiveBarResponse> bars, int period)
    {
        if (bars.Count < period + 1) return null;
        double sum = 0;
        for (int i = bars.Count - period; i < bars.Count; i++)
        {
            double high = (double)bars[i].High, low = (double)bars[i].Low, previous = (double)bars[i - 1].Close;
            sum += Math.Max(high - low, Math.Max(Math.Abs(high - previous), Math.Abs(low - previous)));
        }

        return sum / period;
    }

    private static double? LastOf(double?[] series) => series.LastOrDefault(v => v is not null);

    private static string Px(decimal value) => value.ToString("#,##0.##", India);

    private static string Pct(decimal fraction) => (fraction * 100).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + "%";

    private static string Num(decimal? value, string format = "0.00") => value is decimal v ? v.ToString(format, CultureInfo.InvariantCulture) : "—";

    private static string Lakh(long? value) => value is long v ? (v / 100_000m).ToString("0.0", CultureInfo.InvariantCulture) + " L" : "—";

    private static string Signed(string value) => value.StartsWith('-') || value == "—" ? value : "+" + value;

    private static string Cut(string? text, int max)
    {
        var flat = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }

    private static string Compact(string json, int max)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Cut(JsonSerializer.Serialize(doc.RootElement), max);
        }
        catch (JsonException)
        {
            return Cut(json, max);
        }
    }
}

/// <summary>Builds the AI Trader's brief as of a moment (<see cref="MarketBriefBuilder"/>; a fake in tests).</summary>
public interface IAiTraderBriefs
{
    Task<MarketBrief> BuildAsync(DateTime asOfUtc, bool replay, CancellationToken cancellationToken);
}

/// <summary>A brief, its moment, and the chains it read (the contracts a buy resolves to).</summary>
public sealed record MarketBrief(DateTime AsOfUtc, bool Replay, string Text, IReadOnlyDictionary<string, OptionChainResponse> Chains);
