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
/// It starts in shadow mode: it decides and places nothing (<see cref="AiSettings.AiTraderExecute"/>). In a
/// market replay that the owner asked it into, it decides on the replay's clock and is judged as on a fresh
/// day, also placing nothing: its account is today's, and a replayed day never traded in it.
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
        // A market replay it was asked into: it decides on the replay's clock.
        var replay = await replays.LoadAsync(cancellationToken);
        if (replay is { AiTrader: true } && MarketReplayService.IsActive(replay.State) && replayBook?.ClockUtc is DateTime clock
            && replayBook.Day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == replay.Date)
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
        var book = await books.ReadAsync(clockUtc, replay, cancellationToken);
        string text = brief.Text + "\n" + AiTraderBookReader.Describe(book, Rules, replay) + "\nDecide now: one JSON object.";

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

        return await SaveAsync(row, cancellationToken);
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
