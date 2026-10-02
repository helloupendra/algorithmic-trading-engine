using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Api.Services.Replay;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiTrader;

/// <summary>
/// The AI Trader learning from its own days (owner, 2 Oct): one step a scheduler minute, a reflection on a finished
/// day (<see cref="AiTraderReflection"/>) or one past look of a lesson's test (<see cref="AiTraderLessonCheck"/>).
/// </summary>
/// <remarks>
/// <para>
/// It runs while the AI Trader is on and <see cref="AiSettings.AiTraderLessons"/> is true, and only outside the NSE
/// session on a trading day (09:00–15:45 IST, from the exchange calendar): the free tier is slow and the live desk
/// comes first. It also waits while a replay the AI Trader decides in is playing, so the replay's looks never wait
/// behind it. Reflections go first, one a minute: each is one Judge call. Then the proposed lessons are tested one
/// at a time, oldest first, one look a minute.
/// </para>
/// <para>
/// "Run now" (<c>POST api/Ai/agents/ai-trader-reflect/run</c>) takes one step at once, or with a replay's id
/// reflects on that replay, under the same hours.
/// </para>
/// </remarks>
public sealed class AiTraderLessonAgent(
    AiTraderReflection reflection,
    AiTraderLessonCheck check,
    AiTraderLessonState state,
    IMarketSessionService sessions,
    IReplaySessions replays,
    IOptionsMonitor<AiSettings> settings,
    ILogger<AiTraderLessonAgent> logger,
    TimeProvider? time = null) : IAiScheduledAgent
{
    /// <summary>Before the NSE open and after its close, the window the lesson work keeps out of on a trading day.</summary>
    public static readonly TimeSpan SessionMargin = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string AgentKey => AiCatalog.AiTraderReflect;

    public async Task<bool> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (await WhyNotAsync(nowUtc, cancellationToken) is not null) return false;
        if (!await state.Gate.WaitAsync(0, cancellationToken)) return false;
        try
        {
            return await StepAsync(nowUtc, cancellationToken);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <summary>One step now ("Run now"); with a replay's id, the reflection on that replay if it is due. Under the same hours.</summary>
    public async Task<AiReport?> RunForAsync(string? subjectId, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (await WhyNotAsync(now, cancellationToken) is string why)
        {
            logger.LogInformation("AI Trader lessons: not run now: {Why}", why);
            return null;
        }

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            if (long.TryParse(subjectId, out long replayId))
            {
                var subject = (await reflection.DueAsync(now, cancellationToken)).FirstOrDefault(s => s.ReplaySessionId == replayId);
                if (subject is not null) return await reflection.ReflectAsync(subject, now, cancellationToken);
                logger.LogInformation("AI Trader lessons: replay {Replay} is not due a reflection (playing, reflected, or no readable look)", replayId);
                return null;
            }

            await StepAsync(now, cancellationToken);
            return null;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <summary>A reflection when a finished day is due, else the next look of a lesson's test.</summary>
    private async Task<bool> StepAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var due = await reflection.DueAsync(nowUtc, cancellationToken);
        if (due.Count > 0)
        {
            await reflection.ReflectAsync(due[0], nowUtc, cancellationToken);
            return true;
        }

        return await check.StepAsync(nowUtc, startNew: true, cancellationToken);
    }

    /// <summary>Why no step may be taken now, or null when one may.</summary>
    public async Task<string?> WhyNotAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue;
        if (!s.AiTraderLessons) return "Ai:AiTraderLessons is off";
        if (!s.HasMemory(AiCatalog.AiTrader)) return "the AI Trader has no memory (Ai:MemoryEnabled, Ai:MemoryAgents)";
        if (InSession(sessions, nowUtc)) return "the NSE session (09:00–15:45 IST on a trading day)";
        var replay = await replays.LoadAsync(cancellationToken);
        if (replay is { AiTrader: true } && MarketReplayService.IsActive(replay.State)) return $"replay #{replay.Id} is playing with the AI Trader";
        return null;
    }

    /// <summary>On an NSE trading day, from 15 minutes before the open to 15 minutes after the close.</summary>
    public static bool InSession(IMarketSessionService sessions, DateTime nowUtc)
    {
        var info = sessions.GetSessionInfo(nowUtc, "NSE", "FO");
        return info.IsTradingDay && nowUtc >= info.SessionOpenUtc - SessionMargin && nowUtc < info.SessionCloseUtc + SessionMargin;
    }
}
