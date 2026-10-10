using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiAgents;

/// <summary>
/// The Strategy Researcher: once a week, offline, it reads the week's backtests, each strategy's paper results after
/// charges and its spec, with the overfitting checks code ran (<see cref="StrategyResearchData"/>), and writes what the
/// checks mean and at most three ideas worth testing. Never a live change.
/// </summary>
/// <remarks>
/// <para>
/// Due on <see cref="AiSettings.ResearchDay"/> (Sunday) from <see cref="AiSettings.ResearchAfterIst"/> (11:00 IST),
/// one note a week (subject <c>week:yyyy-MM-dd</c>, the Sunday); a Sunday the API missed is written at the next chance
/// that week, never inside a session (<see cref="AiAgentHours.InSession"/>), so a Sunday special session waits too.
/// </para>
/// <para>
/// The answer is held to the tables (<see cref="ParseNote"/>): its strategies and check ids must be the code's, at
/// most three ideas, none a live change, and every number, time and date it writes must be in what it was handed.
/// A week with no backtest and no paper run asks no model.
/// </para>
/// </remarks>
public sealed class StrategyResearcherAgent(
    AiGateway gateway,
    AiReportWriter reports,
    StrategyResearchData data,
    IMarketSessionService sessions,
    AiSchedulerState schedule,
    IOptionsMonitor<AiSettings> settings,
    ILogger<StrategyResearcherAgent> logger,
    TimeProvider? time = null) : IAiScheduledAgent
{
    public const string SubjectPrefix = "week:";

    public const int MaxIdeas = 3;

    public static readonly IReadOnlySet<string> Verdicts = new HashSet<string>(StringComparer.Ordinal) { "concern", "fine", "unknown" };

    /// <summary>Words that make an "idea" a live change rather than a test: such an answer is invalid.</summary>
    private static readonly string[] LiveWords =
        ["go live", "goes live", "deploy", "start it live", "run it live", "put it live", "switch it on", "in the live account", "live run now"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string AgentKey => AiCatalog.StrategyResearcher;

    public async Task<bool> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var week = await NextDueAsync(nowUtc, cancellationToken);
        if (week is null) return false;
        await WriteAsync(week.Value, nowUtc, cancellationToken);
        schedule.Worked(AgentKey, nowUtc);
        return true;
    }

    /// <summary>"Run now": the note of the week named (<c>week:yyyy-MM-dd</c> or a day), or of the latest week, rewritten. Never inside a session.</summary>
    public async Task<AiReport?> RunForAsync(string? subjectId, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (AiAgentHours.InSession(now, sessions)) return null;
        string text = (subjectId ?? string.Empty).Trim();
        if (text.StartsWith(SubjectPrefix, StringComparison.Ordinal)) text = text[SubjectPrefix.Length..];
        var week = DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : LatestWeek(now);
        return await WriteAsync(week, now, cancellationToken);
    }

    /// <summary>The week (its <see cref="AiSettings.ResearchDay"/>) whose note is owed now, or null.</summary>
    public async Task<DateOnly?> NextDueAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (AiAgentHours.InSession(nowUtc, sessions)) return null;
        var week = LatestWeek(nowUtc);
        // The latest week only: a note older than that is not worth writing late.
        if (IstTime.DateOf(nowUtc).DayNumber - week.DayNumber >= 7) return null;
        var due = await reports.DueAsync(AgentKey, AiReportSubject.Check, [Subject(week)], cancellationToken);
        return due.Count > 0 ? week : null;
    }

    private DateOnly LatestWeek(DateTime nowUtc) =>
        AiAgentHours.LatestWeekly(nowUtc, settings.CurrentValue.ResearchDay, AiAgentHours.Clock(settings.CurrentValue.ResearchAfterIst, new TimeOnly(11, 0)));

    /// <summary>The report subject of a week's note.</summary>
    public static string Subject(DateOnly week) => SubjectPrefix + week.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<AiReport> WriteAsync(DateOnly week, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var at = AiAgentHours.Clock(settings.CurrentValue.ResearchAfterIst, new TimeOnly(11, 0));
        var toUtc = IstTime.FromIst(week.ToDateTime(at));
        if (toUtc > nowUtc) toUtc = nowUtc;
        var fromUtc = toUtc.AddDays(-7);
        string subject = Subject(week);
        string label = $"Research note, week to {week:ddd d MMM}";

        ResearchWeek tables;
        try
        {
            tables = await data.BuildAsync(week, fromUtc, toUtc, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The research tables for {Week} stopped on the desk's side; kept as a failed try", subject);
            return await reports.SaveAsync(AgentKey, AiReportSubject.Check, subject, week, AiReportStatus.Failed, null,
                $"{label}: not written", string.Empty, "{}", $"The tables stopped on the desk's side: {ex.GetType().Name}", cancellationToken);
        }

        if (tables.Backtests.Count == 0 && tables.Paper.Count == 0)
        {
            var empty = TablesData(tables);
            return await reports.SaveAsync(AgentKey, AiReportSubject.Check, subject, week, AiReportStatus.Ok, null,
                $"{label}: no backtest and no paper run to read", "No backtest finished and no strategy ran on paper this week: nothing to research.",
                empty.ToJsonString(Json), string.Empty, cancellationToken);
        }

        string question = Question(tables);
        var result = await gateway.AskAsync(new AiAskInput(
            AgentKey, null, [new AiMessage("user", question)], null, 8000, 0.2,
            $"research-{subject}", "schedule", AgentKey, null), NullAiStreamSink.Instance, cancellationToken);

        if (result.Outcome != AiCallOutcome.Ok)
        {
            string why = result.RefusalStatus is null ? result.Error : $"Refused: {result.Error}";
            if (AiGateway.IsCapacityRefusal(result))
            {
                logger.LogInformation("Research note {Week} was turned away for capacity; tried again in the next window: {Why}", subject, why);
                return await reports.SaveTurnedAwayAsync(AgentKey, AiReportSubject.Check, subject, week, result, $"{label}: not written",
                    $"Turned away for capacity, not counted as a try: {why}", cancellationToken);
            }

            logger.LogInformation("Research note {Week}: no model answered: {Why}", subject, why);
            return await reports.SaveAsync(AgentKey, AiReportSubject.Check, subject, week, AiReportStatus.Failed, result,
                $"{label}: not written", string.Empty, TablesData(tables).ToJsonString(Json), why, cancellationToken);
        }

        var note = ParseNote(result.Text, tables, AiCatalog.Agent(AgentKey)!.SystemPrompt + "\n" + question);
        var dataJson = TablesData(tables);
        if (note.Problems.Count > 0)
        {
            return await reports.SaveAsync(AgentKey, AiReportSubject.Check, subject, week, AiReportStatus.Invalid, result,
                $"{label}: the note failed its check", result.Text, dataJson.ToJsonString(Json), string.Join("; ", note.Problems.Take(6)), cancellationToken);
        }

        dataJson["summary"] = note.Summary;
        dataJson["strategyNotes"] = note.Strategies.DeepClone();
        dataJson["ideas"] = note.Ideas.DeepClone();
        int concerns = tables.Checks.Count(c => c.Reading == "concern");
        string title = $"{label}: {tables.Strategies.Count} strateg{(tables.Strategies.Count == 1 ? "y" : "ies")}, {concerns} concern{(concerns == 1 ? "" : "s")}, " +
                       $"{note.Ideas.Count} idea{(note.Ideas.Count == 1 ? "" : "s")} to test";
        return await reports.SaveAsync(AgentKey, AiReportSubject.Check, subject, week, AiReportStatus.Ok, result,
            title, Body(tables, note), dataJson.ToJsonString(Json), string.Empty, cancellationToken);
    }

    /// <summary>Backtest rows handed to the model at most, newest first; the checks count every one.</summary>
    public const int MaxBacktestsListed = 80;

    /// <summary>What the model is handed: the rules, the backtests, the paper results, the code's checks and the specs.</summary>
    public static string Question(ResearchWeek t)
    {
        var payload = new
        {
            week = t.Week.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            backtestsNotListed = t.Backtests.Count > MaxBacktestsListed ? t.Backtests.Count - MaxBacktestsListed : (int?)null,
            backtests = t.Backtests.Take(MaxBacktestsListed).Select(b => new
            {
                run = b.RunId, strategy = b.Strategy, underlying = b.Underlying, resolution = b.Resolution, from = b.From, to = b.To,
                periods = b.Periods, trades = b.Trades, net = b.Net, perTrade = b.PerTrade, winRate = b.WinRate, tunables = b.Tunables,
                config = b.Config, status = b.Status,
            }),
            paper = t.Paper.Select(p => new
            {
                strategy = p.Strategy, period = p.Period, runs = p.Runs, days = p.Days, trades = p.Trades, netAfterCharges = p.Net,
                charges = p.Charges, perTrade = p.PerTrade,
            }),
            checks = t.Checks.Select(c => new { strategy = c.Strategy, check = c.Check, code = c.Reading, facts = c.Facts }),
            specs = t.Specs.Select(s => new { strategy = s.Strategy, idea = s.Idea, limitations = s.Limitations }),
        };
        return "Write this week's research note.\n" + StrategyResearchData.Rules + "\n" + JsonSerializer.Serialize(payload, Json);
    }

    /// <summary>The note as read, and every reason it fails its check (none when it passes).</summary>
    public sealed record ParsedNote(string? Summary, JsonArray Strategies, JsonArray Ideas, List<string> Problems);

    /// <summary>
    /// Reads the model's note and holds it to the tables: the strategies and checks the code listed, a verdict from
    /// the list, at most <see cref="MaxIdeas"/> ideas each a test and not a live change, and every number, time and
    /// date one of <paramref name="given"/>'s.
    /// </summary>
    public static ParsedNote ParseNote(string answer, ResearchWeek tables, string given)
    {
        var problems = new List<string>();
        var obj = AiJson.Object(answer);
        if (obj is null)
        {
            return new ParsedNote(null, [], [], ["The answer was not the JSON object asked for."]);
        }

        var known = tables.Strategies.ToHashSet(StringComparer.Ordinal);
        var checksOf = tables.Checks.GroupBy(c => c.Strategy).ToDictionary(g => g.Key, g => g.Select(c => c.Check).ToHashSet(StringComparer.Ordinal));
        var strategies = new JsonArray();
        foreach (var s in (obj["strategies"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string name = AiJson.Str(s, "strategy") ?? string.Empty;
            if (!known.Contains(name))
            {
                problems.Add($"it writes about a strategy the tables do not list (\"{Clip(name, 60)}\")");
                continue;
            }

            var checks = new JsonArray();
            foreach (var c in (s["checks"] as JsonArray ?? []).OfType<JsonObject>())
            {
                string check = AiJson.Str(c, "check") ?? string.Empty;
                string verdict = AiJson.Str(c, "verdict")?.ToLowerInvariant() ?? string.Empty;
                string note = AiJson.Str(c, "note") ?? string.Empty;
                if (!checksOf[name].Contains(check)) problems.Add($"{name}: \"{Clip(check, 40)}\" is not one of its checks");
                if (!Verdicts.Contains(verdict)) problems.Add($"{name}: verdict \"{Clip(verdict, 20)}\" is not concern, fine or unknown");
                Numbers(note, given, $"{name}", problems);
                checks.Add(new JsonObject { ["check"] = check, ["verdict"] = verdict, ["note"] = note });
            }

            string summary = AiJson.Str(s, "summary") ?? string.Empty;
            Numbers(summary, given, name, problems);
            strategies.Add(new JsonObject { ["strategy"] = name, ["checks"] = checks, ["summary"] = summary });
        }

        var ideas = new JsonArray();
        var given_ideas = (obj["ideas"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (given_ideas.Count > MaxIdeas) problems.Add($"{given_ideas.Count} ideas, more than {MaxIdeas}");
        foreach (var i in given_ideas)
        {
            string name = AiJson.Str(i, "strategy") ?? string.Empty;
            string idea = AiJson.Str(i, "idea") ?? string.Empty;
            string test = AiJson.Str(i, "test") ?? string.Empty;
            string why = AiJson.Str(i, "why") ?? string.Empty;
            if (!known.Contains(name)) problems.Add($"an idea names a strategy the tables do not list (\"{Clip(name, 60)}\")");
            if (idea.Length == 0 || test.Length == 0) problems.Add("an idea without the test to run");
            string all = $"{idea} {test} {why}".ToLowerInvariant();
            if (LiveWords.FirstOrDefault(w => all.Contains(w, StringComparison.Ordinal)) is string live)
            {
                problems.Add($"an idea is a live change (\"{live}\"), not a test");
            }

            Numbers($"{idea}\n{test}\n{why}", given, "an idea", problems);
            ideas.Add(new JsonObject { ["strategy"] = name, ["idea"] = idea, ["test"] = test, ["why"] = why });
        }

        string? overall = AiJson.Str(obj, "summary");
        Numbers(overall, given, "summary", problems);
        if (strategies.Count == 0 && known.Count > 0 && problems.Count == 0) problems.Add("it wrote about none of the strategies");
        return new ParsedNote(overall, strategies, ideas, problems.Distinct().ToList());
    }

    private static void Numbers(string? text, string given, string where, List<string> problems)
    {
        foreach (var bad in AiFactCheck.Unsupported(text, given).Take(3)) problems.Add($"{where}: \"{bad}\" is not in the tables");
    }

    private static JsonObject TablesData(ResearchWeek t) => new()
    {
        ["week"] = t.Week.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["from"] = IstTime.ToIst(t.FromUtc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        ["to"] = IstTime.ToIst(t.ToUtc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        ["backtests"] = JsonSerializer.SerializeToNode(t.Backtests, Json),
        ["paper"] = JsonSerializer.SerializeToNode(t.Paper, Json),
        ["checks"] = JsonSerializer.SerializeToNode(t.Checks, Json),
    };

    private static string Body(ResearchWeek t, ParsedNote note)
    {
        var body = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(note.Summary)) body.Append(note.Summary!.Trim()).Append("\n\n");
        foreach (var s in note.Strategies.OfType<JsonObject>())
        {
            string name = AiJson.Str(s, "strategy") ?? string.Empty;
            body.Append($"**{name}.** {AiJson.Str(s, "summary")}\n");
            foreach (var c in (s["checks"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var facts = t.Checks.FirstOrDefault(x => x.Strategy == name && x.Check == AiJson.Str(c, "check"))?.Facts;
                body.Append($"- {AiJson.Str(c, "check")} ({AiJson.Str(c, "verdict")}): {AiJson.Str(c, "note")}");
                if (facts is not null) body.Append($" _{facts}_");
                body.Append('\n');
            }

            body.Append('\n');
        }

        if (note.Ideas.Count > 0)
        {
            body.Append("**Worth testing** (a backtest each, never a live change)\n");
            int n = 1;
            foreach (var i in note.Ideas.OfType<JsonObject>())
            {
                body.Append($"{n++}. **{AiJson.Str(i, "strategy")}**: {AiJson.Str(i, "idea")} Test: {AiJson.Str(i, "test")} Why: {AiJson.Str(i, "why")}\n");
            }
        }

        body.Append($"\n_{t.Backtests.Count} backtests and {t.Paper.Count(p => p.Period.EndsWith("30 days", StringComparison.Ordinal))} strategies on paper read; " +
                    "the numbers are the code's, in the report's data._");
        return body.ToString().TrimEnd();
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
