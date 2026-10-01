using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>The AI Trader's record as the console reads it, a page at a time.</summary>
public class AiTraderControllerTests
{
    private static readonly DateOnly Day = new(2026, 9, 30);

    [Fact]
    public async Task Paging_a_days_decisions_reaches_every_look_when_a_replay_of_that_day_shares_its_clock()
    {
        var ai = Build(Settings());
        // The live shadow day first (ids 1–10), then a replay of the same day on a later evening (ids 11–20): same clocks.
        for (int i = 0; i < 10; i++) Look(ai, null, 9 * 60 + 20 + 10 * i);
        for (int i = 0; i < 10; i++) Look(ai, 4, 9 * 60 + 20 + 10 * i);
        await ai.Db.SaveChangesAsync();
        var controller = new AiTraderController(ai.Db, ai.Store, ai.Options);

        var seen = new List<long>();
        long? before = null;
        for (int page = 0; page < 10; page++)
        {
            var body = Assert.IsType<OkObjectResult>(await controller.Decisions("2026-09-30", null, take: 5, beforeId: before)).Value!;
            var items = (IReadOnlyList<AiTraderDecisionSummary>)body.GetType().GetProperty("items")!.GetValue(body)!;
            seen.AddRange(items.Select(d => d.Id));
            before = (long?)body.GetType().GetProperty("nextBeforeId")!.GetValue(body);
            if (before is null) break;
        }

        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i).Order(), seen.Order());
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    private static void Look(Services ai, long? replay, int minuteOfDay) => ai.Db.AiTraderDecisions.Add(new AiTraderDecision
    {
        CreatedUtc = DateTime.UtcNow, ClockUtc = IstTime.FromIst(Day.ToDateTime(new TimeOnly(minuteOfDay / 60, minuteOfDay % 60))), Day = Day,
        Mode = replay is null ? AiTraderModes.Shadow : AiTraderModes.Replay, ReplaySessionId = replay, Action = AiTraderPlan.None, Rule = "ok", Allowed = true,
    });
}
