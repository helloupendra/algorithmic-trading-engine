using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Api.Services.Replay;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiTrader;

/// <summary>
/// The AI Trader (owner, 1 Oct 2026): every ten minutes of the session it reads a market brief built by code,
/// the model proposes one action as JSON, <see cref="AiTraderGuard"/> judges it against the owner's limits, and
/// every decision is kept, "do nothing" included (<c>ai_trader_decisions</c>).
/// </summary>
/// <remarks>
/// <para>
/// It starts in shadow mode: it decides and places nothing (<see cref="AiSettings.AiTraderExecute"/>). Its
/// allowed buys go into its shadow book instead (<see cref="AiTraderShadowBook"/>), checked every minute
/// against their stops and targets, so a shadow day can be scored after charges and the model sees what it
/// holds. In a market replay that the owner asked it into, it decides on the replay's clock with a fresh
/// shadow book of that replay's own.
/// </para>
/// <para>
/// No model holds an order tool. The model's answer only proposes; the code checks it and, once execution
/// is on, places it in its own account.
/// </para>
/// </remarks>
public sealed class AiTraderAgent(
    TradingDbContext db,
    AiGateway gateway,
    IAiTraderBriefs briefs,
    IAiTraderBooks books,
    AiTraderShadowBook shadow,
    IMarketSessionService sessions,
    IReplaySessions replays,
    IOptionsMonitor<AiSettings> settings,
    ILogger<AiTraderAgent> logger,
    IMarketReplayBook? replayBook = null,
    TimeProvider? time = null) : IAiScheduledAgent
{
    /// <summary>The owner's limits; <see cref="AiTraderRules"/> holds the defaults.</summary>
    public static readonly AiTraderRules Rules = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string AgentKey => AiCatalog.AiTrader;

    private TimeSpan Every => TimeSpan.FromMinutes(Math.Max(1, settings.CurrentValue.AiTraderEveryMinutes));

    public async Task<bool> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var replay = await replays.LoadAsync(cancellationToken);
        bool replaying = replay is { AiTrader: true } && MarketReplayService.IsActive(replay.State) && replayBook?.ClockUtc is not null
            && replayBook.Day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == replay.Date;
        await CheckShadowAsync(nowUtc, replay, replaying, cancellationToken);

        // A market replay it was asked into: it decides on the replay's clock.
        if (replaying && replayBook?.ClockUtc is DateTime clock)
        {
            if (!InLoopHours(clock)) return false;
            var lastReplayed = await db.AiTraderDecisions.AsNoTracking()
                .Where(d => d.ReplaySessionId == replay.Id).MaxAsync(d => (DateTime?)d.ClockUtc, cancellationToken);
            if (lastReplayed is DateTime lr && clock - lr < Every) return false;
            await DecideAsync(clock, AiTraderModes.Replay, replay.Id, cancellationToken);
            return true;
        }

        if (!sessions.GetSessionInfo(nowUtc, "NSE", "FO").IsTradingDay || !InLoopHours(nowUtc)) return false;
        var day = IstTime.DateOf(nowUtc);
        var last = await db.AiTraderDecisions.AsNoTracking()
            .Where(d => d.Day == day && d.ReplaySessionId == null).MaxAsync(d => (DateTime?)d.ClockUtc, cancellationToken);
        if (last is DateTime l && nowUtc - l < Every) return false;

        await DecideAsync(nowUtc, settings.CurrentValue.AiTraderExecute ? AiTraderModes.Live : AiTraderModes.Shadow, null, cancellationToken);
        return true;
    }

    /// <summary>
    /// Every minute, whatever the decision schedule: the shadow book's open positions against their stops,
    /// targets and the close, live and in the replay that is playing; an ended replay's are closed. A failure
    /// here is logged and never costs a decision.
    /// </summary>
    private async Task CheckShadowAsync(DateTime nowUtc, ReplaySessionState? replay, bool replaying, CancellationToken cancellationToken)
    {
        try
        {
            await shadow.EndReplaysAsync(replay is not null && MarketReplayService.IsActive(replay.State) ? replay.Id : null, cancellationToken);
            if (replaying && replayBook?.ClockUtc is DateTime clock) await shadow.CheckAsync(clock, replay!.Id, cancellationToken);
            await shadow.CheckAsync(nowUtc, null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AI Trader: the shadow book's minute check failed");
        }
    }

    /// <summary>One decision now, outside the schedule ("Run now" on AI → Agents). The rules judge it as they would at this hour.</summary>
    public async Task<AiReport?> RunForAsync(string? subjectId, CancellationToken cancellationToken)
    {
        await DecideAsync(_time.GetUtcNow().UtcDateTime,
            settings.CurrentValue.AiTraderExecute ? AiTraderModes.Live : AiTraderModes.Shadow, null, cancellationToken);
        return null;
    }

    private bool InLoopHours(DateTime utc)
    {
        var s = settings.CurrentValue;
        var ist = TimeOnly.FromDateTime(IstTime.ToIst(utc));
        var from = TimeOnly.TryParseExact(s.AiTraderFromIst, "HH:mm", out var f) ? f : new TimeOnly(9, 20);
        var until = TimeOnly.TryParseExact(s.AiTraderUntilIst, "HH:mm", out var u) ? u : new TimeOnly(15, 0);
        return ist >= from && ist <= until;
    }

    /// <summary>Builds the brief, asks the model, judges the plan, and keeps the decision. Public for tests.</summary>
    public async Task<AiTraderDecision> DecideAsync(DateTime clockUtc, string mode, long? replaySessionId, CancellationToken cancellationToken)
    {
        bool replay = mode == AiTraderModes.Replay;
        var brief = await briefs.BuildAsync(clockUtc, replay, cancellationToken);
        var account = await books.ReadAsync(clockUtc, replay, cancellationToken);
        var book = mode == AiTraderModes.Live ? account : await shadow.ReadAsync(account, replaySessionId, cancellationToken);
        var day = IstTime.DateOf(clockUtc);
        var earlier = await db.AiTraderDecisions.AsNoTracking()
            .Where(d => d.ClockUtc < clockUtc && (replaySessionId == null ? d.Day == day && d.ReplaySessionId == null : d.ReplaySessionId == replaySessionId))
            .OrderByDescending(d => d.ClockUtc).ThenByDescending(d => d.Id).Take(LastLooksShown)
            .ToListAsync(cancellationToken);
        string text = brief.Text + "\n" + AiTraderBookReader.Describe(book, Rules, mode) + "\n" + LastLooks(earlier)
                      + "\nDecide now: one JSON object.";

        var row = new AiTraderDecision
        {
            CreatedUtc = _time.GetUtcNow().UtcDateTime,
            ClockUtc = clockUtc,
            Day = IstTime.DateOf(clockUtc),
            Mode = mode,
            ReplaySessionId = replaySessionId,
            BriefHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32],
            Brief = text,
        };

        string conversation = $"ai-trader-{IstTime.ToIst(clockUtc):yyyyMMdd-HHmm}" + (replaySessionId is long r ? $"-replay{r}" : string.Empty);
        var result = await gateway.AskAsync(new AiAskInput(
            AgentKey, null, [new AiMessage("user", text)], null, 4000, 0.2, conversation, "schedule", AgentKey, null),
            NullAiStreamSink.Instance, cancellationToken);
        row.CallId = result.CallId;
        row.Model = result.Model;

        if (result.Outcome != AiCallOutcome.Ok)
        {
            row.Rule = "no-answer";
            row.Error = Cut(result.RefusalStatus is null ? result.Error : $"Refused: {result.Error}", 1000);
            logger.LogInformation("AI Trader: no answer at {Clock} ({Error})", IstTime.ToIst(clockUtc).ToString("HH:mm", CultureInfo.InvariantCulture), row.Error);
            return await SaveAsync(row, cancellationToken);
        }

        var (plan, error) = AiTraderPlanReader.Read(result.Text);
        if (plan is null)
        {
            row.Rule = "unreadable";
            row.Why = Cut(error ?? "The answer was not a plan.", 1000);
            row.PlanJson = JsonSerializer.Serialize(new { answer = Cut(result.Text, 2000) }, Json);
            return await SaveAsync(row, cancellationToken);
        }

        row.Action = plan.Action;
        row.Underlying = plan.Underlying ?? string.Empty;
        row.Reason = plan.Reason;
        row.Confidence = plan.Confidence;
        row.PlanJson = JsonSerializer.Serialize(plan, Json);

        var contract = plan.Action == AiTraderPlan.Buy ? Resolve(plan, brief, replay) : null;
        var verdict = AiTraderGuard.Check(plan, book, Rules, contract);
        row.Allowed = verdict.Allowed;
        row.Rule = verdict.Rule;
        row.Why = Cut(verdict.Why, 1000);
        if (contract is not null) row.ResultJson = JsonSerializer.Serialize(new { contract }, Json);

        if (verdict.Allowed && plan.Action != AiTraderPlan.None && mode == AiTraderModes.Live)
        {
            // Execution comes with its own step: until then a live decision is kept like a shadow one.
            row.Error = "Execution is not switched on in this build: nothing was placed.";
        }

        await SaveAsync(row, cancellationToken);
        if (verdict.Allowed && mode != AiTraderModes.Live) await ApplyToShadowAsync(row, plan, contract, cancellationToken);
        return row;
    }

    /// <summary>An allowed plan in shadow or replay: a buy opens in the shadow book, an exit closes there; strategy starts and stops are recorded only.</summary>
    private async Task ApplyToShadowAsync(AiTraderDecision row, AiTraderPlan plan, AiTraderContract? contract, CancellationToken cancellationToken)
    {
        object? result = plan.Action switch
        {
            AiTraderPlan.Buy when contract is not null =>
                new { contract, shadowPositionId = (await shadow.OpenAsync(row, plan, contract, cancellationToken)).Id },
            AiTraderPlan.Exit when plan.PositionId is long id =>
                await shadow.ExitAsync(id, row.ReplaySessionId, row.ClockUtc, cancellationToken) is { } closed
                    ? new { shadowPositionId = closed.Id, closed.ExitPrice, closed.Charges, closed.NetPnl }
                    : new { shadowPositionId = id, note = "It was no longer open: closed by its stop, target or the close before this look." },
            AiTraderPlan.StartStrategy or AiTraderPlan.StopStrategy =>
                new { note = "Strategy runs are not simulated in shadow: recorded only." },
            _ => null,
        };
        if (result is null) return;
        row.ResultJson = JsonSerializer.Serialize(result, Json);
        await db.SaveChangesAsync(cancellationToken);
    }

    private const int LastLooksShown = 3;

    /// <summary>
    /// Its last looks of the day (or of the replay), newest first: what it proposed and what the rules said, so a
    /// refusal can be corrected on the next look rather than repeated.
    /// </summary>
    public static string LastLooks(IReadOnlyList<AiTraderDecision> newestFirst)
    {
        if (newestFirst.Count == 0) return "YOUR LAST LOOKS: none yet today.";
        var lines = newestFirst.Select(d =>
        {
            string at = IstTime.ToIst(d.ClockUtc).ToString("HH:mm", CultureInfo.InvariantCulture);
            if (d.Rule is "no-answer" or "unreadable") return $"- {at} no usable answer.";
            string what = d.Action == AiTraderPlan.Buy ? BuyText(d.PlanJson) : $"{d.Action} {d.Underlying}".TrimEnd();
            string verdict = !d.Allowed ? $"refused ({d.Rule}): {Cut(d.Why, 200)}"
                : d.Action == AiTraderPlan.None ? "nothing to judge"
                : d.Executed ? "allowed and placed"
                : d.Mode == AiTraderModes.Live ? "allowed, not placed"
                : d.Action == AiTraderPlan.Buy ? "allowed, opened in your shadow book"
                : d.Action == AiTraderPlan.Exit ? "allowed, closed in your shadow book"
                : "allowed, recorded only (strategy runs are not simulated in shadow)";
            return $"- {at} {what} → {verdict}";
        });
        return "YOUR LAST LOOKS (newest first)\n" + string.Join('\n', lines);
    }

    private static string BuyText(string planJson)
    {
        try
        {
            if (JsonSerializer.Deserialize<AiTraderPlan>(planJson, Json) is { } p)
            {
                return string.Create(CultureInfo.InvariantCulture,
                    $"buy {p.Underlying} {p.Strike} {p.Option}, {p.Lots} lot(s), stop {p.StopLoss}, target {p.Target}");
            }
        }
        catch (JsonException)
        {
            // An old or cut plan: the action alone.
        }

        return "buy";
    }

    /// <summary>
    /// The contract a buy names, from the chain the brief read: the strike around the money on the chain's own
    /// grid, priced at its ask, else its last trade. In a replay the replay's own quote, fresher than the
    /// recorded chain, wins.
    /// </summary>
    public AiTraderContract? Resolve(AiTraderPlan plan, MarketBrief brief, bool replay)
    {
        if (plan.Underlying is null || !brief.Chains.TryGetValue(plan.Underlying, out var chain) || chain.Strikes.Count == 0) return null;
        var strikes = chain.Strikes.Select(s => s.StrikePrice).Distinct().OrderBy(s => s).ToList();
        decimal atm = chain.AtTheMoneyStrike ?? chain.Strikes.FirstOrDefault(s => s.IsAtTheMoney)?.StrikePrice ?? 0m;
        if (atm <= 0 || strikes.Count < 2) return null;
        decimal step = strikes.Zip(strikes.Skip(1), (a, b) => b - a).Where(d => d > 0).DefaultIfEmpty(0m).Min();
        if (AiTraderPlanReader.StrikeOf(plan.Strike, atm, step) is not decimal strike) return null;

        var row = chain.Strikes.FirstOrDefault(s => s.StrikePrice == strike);
        var leg = plan.Option == "CE" ? row?.Call : plan.Option == "PE" ? row?.Put : null;
        if (leg is null || string.IsNullOrWhiteSpace(leg.Symbol)) return null;

        decimal? price = leg.AskPrice is > 0 ? leg.AskPrice : leg.LastTradedPrice;
        if (replay && replayBook?.Quote(leg.Symbol) is { } replayed)
        {
            price = replayed.AskPrice is > 0 ? replayed.AskPrice : replayed.LastTradedPrice ?? price;
        }

        int lot = chain.Header?.LotSize ?? 0;
        return price is > 0 && lot > 0
            ? new AiTraderContract(leg.Symbol, plan.Underlying, plan.Option!, strike, chain.ExpiryDate, price.Value, lot)
            : null;
    }

    private async Task<AiTraderDecision> SaveAsync(AiTraderDecision row, CancellationToken cancellationToken)
    {
        db.AiTraderDecisions.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("AI Trader {Mode} at {Clock}: {Action} {Underlying} → {Rule} ({Why})", row.Mode,
            IstTime.ToIst(row.ClockUtc).ToString("HH:mm", CultureInfo.InvariantCulture), row.Action, row.Underlying, row.Rule, Cut(row.Why, 120));
        return row;
    }

    private static string Cut(string? text, int max)
    {
        var t = (text ?? string.Empty).Trim();
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }
}
