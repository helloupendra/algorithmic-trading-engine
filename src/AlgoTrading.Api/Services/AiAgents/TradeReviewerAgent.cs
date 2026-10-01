using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services.AiTools;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiAgents;

/// <summary>
/// The Trade Reviewer: after the close, one journal per stopped run, judged
/// against the strategy's written spec. Its reviews reach Telegram in the AI's
/// one daily digest (<see cref="AiDailyDigest"/>).
/// </summary>
/// <remarks>
/// <para>
/// A run is due once it has been stopped ten minutes (its last fills and
/// charges are in) and its day's <see cref="AiSettings.ReviewAfterIst"/> has
/// passed, so the NSE runs are reviewed after 15:45 and the MCX runs after
/// they stop at 23:30. Runs from the last two days only: the reviewer is not a
/// backfill. Manual books and alert-only runs are not strategies and are left
/// out.
/// </para>
/// <para>
/// The model is handed the run's summary and the spec as data (the same
/// <c>get_run</c> and <c>get_strategy_spec</c> answers a tool call would
/// give), and may read more with its tools. The verdict is its own; nothing
/// acts on it.
/// </para>
/// <para>
/// It is also handed the market on the run's own day: the underlying's
/// session from its recorded minute bars, and its price when the run stopped.
/// The quote and chain tools answer for now, not for that day. On 1 Oct,
/// catching up on a 29 Sep run, the reviewer took the BANKNIFTY of 1 Oct for
/// 29 Sep's close and called a correct expiry fill (a worthless 54300 CE at
/// ₹0.05) a stale-quote loss.
/// </para>
/// </remarks>
public sealed class TradeReviewerAgent(
    TradingDbContext db,
    AiGateway gateway,
    AiReportWriter reports,
    AiToolbox toolbox,
    AiSchedulerState schedule,
    IOptionsMonitor<AiSettings> settings,
    ILogger<TradeReviewerAgent> logger,
    TimeProvider? time = null) : IAiScheduledAgent
{
    /// <summary>A run is reviewed this long after it stops, so its last fills and charges are in.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMinutes(10);

    /// <summary>How far back a stopped run is still reviewed.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(2);

    private static readonly string[] Stopped = ["Stopped", "Completed", "Failed"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string AgentKey => AiCatalog.TradeReviewer;

    public async Task<bool> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        long? runId = await NextDueAsync(nowUtc, cancellationToken);
        if (runId is null) return false;

        await ReviewAsync(runId.Value, cancellationToken);
        schedule.Worked(AgentKey, nowUtc);
        return true;
    }

    public async Task<AiReport?> RunForAsync(string? subjectId, CancellationToken cancellationToken)
    {
        long? runId = long.TryParse(subjectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
            ? id
            : await NextDueAsync(_time.GetUtcNow().UtcDateTime, cancellationToken);
        return runId is null ? null : await ReviewAsync(runId.Value, cancellationToken);
    }

    /// <summary>The oldest stopped run that is due a review, or null.</summary>
    public async Task<long?> NextDueAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var ready = await ReadyAsync(db, ReviewAfter(settings.CurrentValue), nowUtc, cancellationToken);
        if (ready.Count == 0) return null;

        var due = await reports.DueAsync(AgentKey, AiReportSubject.Run, ready.Select(Id).ToList(), cancellationToken);
        return ready.FirstOrDefault(id => due.Contains(Id(id))) is long next && next != 0 ? next : null;
    }

    /// <summary>
    /// The stopped runs a review is for, oldest first, reviewed or not: stopped
    /// <see cref="Settle"/> ago and within <see cref="Window"/>, past
    /// <paramref name="reviewAfter"/> on their day, and neither a manual book,
    /// an alert run nor a recap. The daily digest waits on these too.
    /// </summary>
    public static async Task<List<long>> ReadyAsync(TradingDbContext db, TimeOnly reviewAfter, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var since = nowUtc - Window;
        var settledBy = nowUtc - Settle;
        var candidates = await db.SimulationRuns.AsNoTracking()
            .Where(r => r.Mode == StrategyRunControl.LivePaperMode
                        && Stopped.Contains(r.Status)
                        && r.CompletedUtc != null && r.CompletedUtc >= since && r.CompletedUtc <= settledBy
                        && r.StrategyName != ManualOrdersController.BookStrategyName
                        // A recap replays another day's session: a test, not a run to journal.
                        && !r.ParametersJson.Contains(RecapRuns.Marker) && !r.ParametersJson.Contains(RecapRuns.SpacedMarker))
            .OrderBy(r => r.CompletedUtc)
            .Select(r => new { r.Id, r.StartedUtc, r.CreatedUtc, r.ParametersJson })
            .ToListAsync(cancellationToken);

        return candidates
            .Where(r => LiveRunParameters.ReadRole(r.ParametersJson) != "alerts")
            .Where(r => nowUtc >= IstTime.FromIst(IstTime.DateOf(r.StartedUtc ?? r.CreatedUtc).ToDateTime(reviewAfter)))
            .Select(r => r.Id)
            .ToList();
    }

    private async Task<AiReport> ReviewAsync(long runId, CancellationToken cancellationToken)
    {
        var run = await db.SimulationRuns.AsNoTracking().FirstAsync(r => r.Id == runId, cancellationToken);
        var day = IstTime.DateOf(run.StartedUtc ?? run.CreatedUtc);

        // The same answers the model would get from its tools, handed over up front.
        string summary = await ToolText(AiToolNames.Run, $$"""{"runId":{{runId}}}""", cancellationToken);
        string spec = await ToolText(AiToolNames.StrategySpec, JsonSerializer.Serialize(new { strategy = run.StrategyName }), cancellationToken);

        string market = await MarketOnDayAsync(run, day, cancellationToken);
        string question =
            $"Review run {runId} ({run.StrategyName}) of {day:yyyy-MM-dd} against its spec.\n\n" +
            $"The market on the run's day ({day:yyyy-MM-dd}, from the desk's recorded minute bars):\n{market}\n\n" +
            $"The run (get_run summary):\n{summary}\n\nThe strategy's spec (get_strategy_spec):\n{spec}";

        var result = await gateway.AskAsync(new AiAskInput(
            AgentKey, null, [new AiMessage("user", question)], null, 6000, 0.2,
            $"review-run-{runId}", "schedule", AgentKey, null), NullAiStreamSink.Instance, cancellationToken);

        if (result.Outcome != AiCallOutcome.Ok)
        {
            string why = result.RefusalStatus is null ? result.Error : $"Refused: {result.Error}";
            // The provider, not the desk: recorded as a failed report and retried.
            logger.LogInformation("Trade review of run {RunId} failed: {Error}", runId, why);
            return await reports.SaveAsync(AgentKey, AiReportSubject.Run, Id(runId), day, AiReportStatus.Failed, result,
                $"Run {runId}: no review", string.Empty, "{}", why, cancellationToken);
        }

        var review = ParseReview(result.Text);
        if (review is null)
        {
            return await reports.SaveAsync(AgentKey, AiReportSubject.Run, Id(runId), day, AiReportStatus.Invalid, result,
                $"Run {runId} ({run.StrategyName}): review not in the asked shape", result.Text, "{}",
                "The answer was not the JSON object asked for; its text is kept as the body.", cancellationToken);
        }

        // "Deviated" is the verdict that teaches the wrong lesson when it is false, and on 1 Oct the same model,
        // given the same data, called run 308 followed and its twin 309 deviated. So a deviation is asked again,
        // separately, and kept only when the second review finds it too.
        if (review.Verdict == "deviated")
        {
            var second = await gateway.AskAsync(new AiAskInput(
                AgentKey, null, [new AiMessage("user", question)], null, 6000, 0.2,
                $"review-run-{runId}-second", "schedule", AgentKey, null), NullAiStreamSink.Instance, cancellationToken);
            if (second.Outcome != AiCallOutcome.Ok)
            {
                string why = "The second review of a deviation got no answer: " + (second.RefusalStatus is null ? second.Error : $"Refused: {second.Error}");
                logger.LogInformation("Trade review of run {RunId}: {Why}", runId, why);
                return await reports.SaveAsync(AgentKey, AiReportSubject.Run, Id(runId), day, AiReportStatus.Failed, second,
                    $"Run {runId}: no review", string.Empty, "{}", why, cancellationToken);
            }

            review = SecondOpinion(review, ParseReview(second.Text), second.CallId);
        }

        return await reports.SaveAsync(AgentKey, AiReportSubject.Run, Id(runId), day, AiReportStatus.Ok, result,
            review.Title, review.Body, review.Data.ToJsonString(Json), string.Empty, cancellationToken);
    }

    /// <summary>
    /// A deviation checked by a second review: kept when the second also finds one, otherwise
    /// "unclear", with both said. Public for tests.
    /// </summary>
    public static ParsedReview SecondOpinion(ParsedReview first, ParsedReview? second, long secondCallId)
    {
        string secondVerdict = second?.Verdict ?? "not in the asked shape";
        var data = (JsonObject)first.Data.DeepClone();
        data["secondReview"] = new JsonObject { ["callId"] = secondCallId, ["verdict"] = secondVerdict };

        if (secondVerdict == "deviated")
        {
            return first with { Body = first.Body + "\n\n**Checked:** a second review, asked the same question separately, also found that it did not follow the spec.", Data = data };
        }

        data["verdict"] = "unclear";
        string said = secondVerdict switch
        {
            "followed" => "found that it followed the spec",
            "unclear" => "could not tell from the records",
            _ => "did not answer in the asked shape",
        };
        string firstBody = first.Body.StartsWith("**Verdict:**", StringComparison.Ordinal) && first.Body.IndexOf("\n\n", StringComparison.Ordinal) is int cut and > 0
            ? first.Body[(cut + 2)..]
            : first.Body;
        string body =
            "**Verdict:** unclear: two reviews disagreed. The first found that the run did not follow the spec; a second, asked the " +
            $"same question separately, {said}. A deviation is kept only when two reviews agree.\n\n**The first review**\n\n{firstBody}" +
            (second is null ? string.Empty : $"\n\n**The second review:** {second.Title}");
        string title = "Reviews disagree: " + first.Title;
        return new ParsedReview(title.Length <= 300 ? title : title[..299] + "…", body, data);
    }

    /// <summary>
    /// The run's underlying on the run's day, from the recorded minute bars:
    /// the session's open, high, low and close, and the last price at or
    /// before the run stopped. In words the model reads as data; says so when
    /// nothing was recorded.
    /// </summary>
    public async Task<string> MarketOnDayAsync(SimulationRun run, DateOnly day, CancellationToken cancellationToken)
    {
        string symbol = run.Symbol;
        bool mcx = symbol.StartsWith("MCX:", StringComparison.OrdinalIgnoreCase);
        var from = IstTime.FromIst(day.ToDateTime(mcx ? new TimeOnly(9, 0) : new TimeOnly(9, 15)));
        var to = IstTime.FromIst(day.ToDateTime(mcx ? new TimeOnly(23, 30) : new TimeOnly(15, 30)));
        var bars = await db.LiveBars.AsNoTracking()
            .Where(b => b.Symbol == symbol && b.Resolution == "1m" && b.BarStartUtc >= from && b.BarStartUtc < to)
            .OrderBy(b => b.BarStartUtc)
            .Select(b => new { b.BarStartUtc, b.Open, b.High, b.Low, b.Close })
            .ToListAsync(cancellationToken);
        if (bars.Count == 0)
        {
            return $"{symbol}: no minute bars were recorded for {day:yyyy-MM-dd}, so the day's prices are not known here. " +
                   "Do not take them from get_quotes or get_option_chain_summary: those answer for now, not for that day.";
        }

        static string Ist(DateTime utc) => IstTime.ToIst(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToString("HH:mm", CultureInfo.InvariantCulture);
        static string Px(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        var first = bars[0];
        var last = bars[^1];
        var high = bars.MaxBy(b => b.High)!;
        var low = bars.MinBy(b => b.Low)!;
        string text =
            $"{symbol}: open {Px(first.Open)} ({Ist(first.BarStartUtc)}), high {Px(high.High)} ({Ist(high.BarStartUtc)}), " +
            $"low {Px(low.Low)} ({Ist(low.BarStartUtc)}), last {Px(last.Close)} ({Ist(last.BarStartUtc)} bar), {bars.Count} minute bars.";
        if (run.CompletedUtc is DateTime stopped)
        {
            var atStop = bars.LastOrDefault(b => b.BarStartUtc <= stopped);
            if (atStop is not null) text += $" At the run's stop ({Ist(stopped)}): {Px(atStop.Close)}.";
        }

        return text + " get_quotes and get_option_chain_summary answer for now, not for this day: use these prices for the run's day.";
    }

    /// <summary>A review the model wrote, read and turned into the report's title, body and data; null when it is not the asked shape.</summary>
    public static ParsedReview? ParseReview(string answer)
    {
        var obj = AiJson.Object(answer);
        if (obj is null) return null;

        string? verdict = AiJson.Str(obj, "verdict")?.ToLowerInvariant();
        string? journal = AiJson.Str(obj, "journal");
        if (verdict is not ("followed" or "deviated" or "unclear") || journal is null) return null;

        string title = AiJson.Str(obj, "title") ?? $"Verdict: {verdict}";
        var followed = AiJson.Strings(obj, "followed");
        var deviations = AiJson.Strings(obj, "deviations");
        double? stale = AiJson.Num(obj, "staleFills");
        string? context = AiJson.Str(obj, "marketContext");
        string? lesson = AiJson.Str(obj, "lesson");

        var body = new StringBuilder();
        body.Append("**Verdict:** ").Append(verdict switch
        {
            "followed" => "followed the spec",
            "deviated" => "did not follow the spec",
            _ => "unclear from the records",
        }).Append("\n\n").Append(journal.Trim()).Append('\n');
        if (deviations.Count > 0) body.Append("\n**Did not follow the spec**\n").Append(string.Concat(deviations.Select(d => $"- {d}\n")));
        if (followed.Count > 0) body.Append("\n**Followed**\n").Append(string.Concat(followed.Select(f => $"- {f}\n")));
        if (stale is > 0) body.Append($"\n**Fills at a stale quote:** {stale:0}\n");
        if (context is not null) body.Append("\n**Market:** ").Append(context).Append('\n');
        if (lesson is not null) body.Append("\n**Worth testing:** ").Append(lesson).Append('\n');

        var data = new JsonObject
        {
            ["verdict"] = verdict,
            ["followed"] = new JsonArray(followed.Select(f => (JsonNode)f).ToArray()),
            ["deviations"] = new JsonArray(deviations.Select(d => (JsonNode)d).ToArray()),
            ["staleFills"] = stale,
            ["marketContext"] = context,
            ["lesson"] = lesson,
        };
        return new ParsedReview(title, body.ToString().TrimEnd(), data);
    }

    public sealed record ParsedReview(string Title, string Body, JsonObject Data)
    {
        public string? Verdict => Data["verdict"]?.GetValue<string>();
    }

    /// <summary>A tool's answer as the model would read it; its error in words when it fails.</summary>
    private async Task<string> ToolText(string name, string args, CancellationToken cancellationToken)
    {
        var tool = toolbox.Find(name);
        if (tool is null) return $"(The {name} tool is not on this build.)";
        try
        {
            var output = await tool.RunAsync(AiToolArgs.Parse(args), cancellationToken);
            return JsonSerializer.Serialize(new { asOf = AiToolFormat.Ist(output.AsOfUtc), data = output.Data }, Json);
        }
        catch (AiToolArgumentException ex)
        {
            return $"(Not available: {ex.Message})";
        }
    }

    /// <summary><see cref="AiSettings.ReviewAfterIst"/>, or 15:45 when it cannot be read.</summary>
    public static TimeOnly ReviewAfter(AiSettings settings) =>
        TimeOnly.TryParseExact(settings.ReviewAfterIst, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at
            : new TimeOnly(15, 45);

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
}
