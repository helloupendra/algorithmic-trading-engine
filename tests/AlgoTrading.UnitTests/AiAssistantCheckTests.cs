using System.Text.Json.Nodes;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;
using Question = AlgoTrading.Api.Services.AiAgents.AssistantCheckAgent.Question;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The Desk Assistant's daily check: questions built from the desk's own
/// tools, asked through the real gateway, graded by code.
/// </summary>
public class AiAssistantCheckTests
{
    // ---------- reading numbers out of an answer ----------

    [Theory]
    [InlineData("Run 339 lost −₹1,10,132.75 net.", -110132.75)]
    [InlineData("Net P&L: -110,132.75", -110132.75)]
    [InlineData("It paid ₹59,774.75 in charges.", 59774.75)]
    [InlineData("about ₹1.1 lakh", 110000)]
    [InlineData("roughly 59.8k in charges", 59800)]
    [InlineData("₹2.5 crore", 25000000)]
    public void Money_is_read_as_the_desk_writes_it(string text, double expected)
    {
        Assert.Contains(AssistantCheckAgent.Numbers(text), x => Math.Abs(x - expected) < 0.01);
    }

    [Fact]
    public void Citations_times_and_dates_are_not_mistaken_for_the_answer()
    {
        var q = new Question("How many open legs?", "number", "10", 10, 0);

        Assert.False(AssistantCheckAgent.Grade(q, "There are 3 open legs (get_open_positions, 20:10 IST) on 2026-09-30."));
        Assert.True(AssistantCheckAgent.Grade(q, "There are 10 open legs (get_open_positions, 20:10 IST)."));
    }

    [Theory]
    [InlineData("The worst run is #339 (Fulcrum).", true)]
    [InlineData("Run 339 lost the most.", true)]
    [InlineData("Run 339.", true)]
    [InlineData("Run 3390 lost the most.", false)]
    [InlineData("It lost 339.5 points.", false)]
    public void A_run_id_counts_only_as_a_whole_number(string answer, bool pass)
    {
        Assert.Equal(pass, AssistantCheckAgent.Grade(new Question("Which run?", "id", "339", 339, 0), answer));
    }

    [Fact]
    public void Money_passes_within_its_tolerance_whatever_its_sign()
    {
        var q = new Question("Net?", "number", "-110,132.75", -110132.75, 1);

        Assert.True(AssistantCheckAgent.Grade(q, "It lost ₹1,10,132 after charges."));
        Assert.False(AssistantCheckAgent.Grade(q, "It lost ₹1,10,000 after charges."));
    }

    [Theory]
    [InlineData("All runs together: −₹1,62,675.07.", true)]  // the second reading
    [InlineData("All runs together: −₹1,62,500.00.", true)]  // between the two
    [InlineData("All runs together: −₹1,63,000.00.", false)]
    public void A_figure_that_moved_while_the_model_answered_passes_anywhere_between_the_two_readings(string answer, bool pass)
    {
        var q = new Question("Total?", "number", "-162,355.07 → -162,675.07", -162355.07, 1, Also: -162675.07);
        Assert.Equal(pass, AssistantCheckAgent.Grade(q, answer));
    }

    [Fact]
    public void A_worst_run_that_changed_while_the_model_answered_passes_as_either()
    {
        var q = new Question("Which run?", "id", "339 → 341", 339, 0, Also: 341);

        Assert.True(AssistantCheckAgent.Grade(q, "Run 341 lost the most."));
        Assert.True(AssistantCheckAgent.Grade(q, "Run 339 lost the most."));
        Assert.False(AssistantCheckAgent.Grade(q, "Run 340 lost the most."));
    }

    // ---------- when it runs ----------

    [Theory]
    [InlineData(2026, 9, 30, 16, 39, false)] // Wednesday, before 16:40
    [InlineData(2026, 9, 30, 16, 41, true)]
    [InlineData(2026, 10, 3, 17, 0, false)]  // Saturday
    public void It_runs_on_weekdays_after_16_40(int y, int m, int d, int h, int min, bool due)
    {
        var ai = Build();
        Assert.Equal(due, Agent(ai).Due(IstTime.FromIst(new DateTime(y, m, d, h, min, 0))));
    }

    [Fact]
    public void It_does_not_run_on_an_exchange_holiday()
    {
        var ai = Build();
        var agent = new AssistantCheckAgent(ai.Db, ai.Gateway, new AiReportWriter(ai.Db, ai.Options), ai.Toolbox, new AiSchedulerState(),
            ai.Options, NullLogger<AssistantCheckAgent>.Instance, new MarketIntelligenceTestKit.Sessions(new DateOnly(2026, 10, 2)));

        Assert.False(agent.Due(IstTime.FromIst(new DateTime(2026, 10, 2, 17, 0, 0)))); // Friday, Gandhi Jayanti
        Assert.True(agent.Due(IstTime.FromIst(new DateTime(2026, 10, 1, 17, 0, 0))));
    }

    [Fact]
    public void It_asks_only_while_the_assistant_is_on_and_it_is_enabled()
    {
        var on = new AiSettings();
        var off = new AiSettings { AssistantCheckEnabled = false };
        var ai = Build();
        var state = ai.Store.LoadAsync().GetAwaiter().GetResult();

        Assert.True(AiAgentScheduler.IsOn(AiCatalog.AssistantCheck, state, on));
        Assert.False(AiAgentScheduler.IsOn(AiCatalog.AssistantCheck, state, off));
    }

    // ---------- the check ----------

    [Fact]
    public async Task The_questions_carry_the_answers_the_desk_s_tools_give()
    {
        var ai = Build(tools: DeskTools());

        var questions = await Agent(ai).QuestionsAsync(CancellationToken.None);

        Assert.Contains(questions, q => q.Kind == "id" && q.Expected == "339");
        Assert.Contains(questions, q => q.Text.Contains("run 339's net") && q.Number == -110132.75);
        Assert.Contains(questions, q => q.Text.Contains("charges") && q.Number == 59774.75);
        Assert.Contains(questions, q => q.Text.Contains("strategy runs") && q.Number == 2);
        Assert.Contains(questions, q => q.Text.Contains("verdict") && q.Expected == "action");
        Assert.Contains(questions, q => q.Text.Contains("put-call ratio") && q.Number == 0.93);
        Assert.Contains(questions, q => q.Number == 5525);
        // Tools that found nothing (no open legs tool here) leave their questions out.
        Assert.DoesNotContain(questions, q => q.Text.Contains("open legs"));
    }

    [Fact]
    public async Task A_day_with_nothing_on_the_desk_asks_nothing()
    {
        var ai = Build();
        Assert.Empty(await Agent(ai).QuestionsAsync(CancellationToken.None));
        Assert.Null(await Agent(ai).RunForAsync(null, CancellationToken.None));
        Assert.Empty(ai.Provider.Requests);
    }

    [Fact]
    public async Task The_day_s_check_is_one_report_with_every_question_graded_and_linked_to_its_call()
    {
        var ai = Build(tools: DeskTools());
        // Answers in the order the questions are asked: runs, total, worst id, worst net, charges, verdict, pcr, max pain, 5525.
        ai.Provider.On(Judge1,
            Answer("There are 2 runs today (get_runs, 16:45 IST)."),
            Answer("Together they made −₹1,00,000."),
            Answer("Run #339 has the lowest net P&L."),
            Answer("Run 339's net is −₹1,10,132.75 after charges."),
            Answer("It paid ₹59,774.75 in charges."),
            Answer("The verdict was ACTION."),
            Answer("NIFTY's PCR is 0.93."),
            Answer("Max pain is 25,000."),
            Answer("₹5,525 profit."));

        var report = await Agent(ai).RunForAsync(null, CancellationToken.None);

        Assert.Equal(AiReportStatus.Ok, report!.Status);
        Assert.Equal(AiReportSubject.Check, report.SubjectType);
        Assert.Equal(IstTime.DateOf(DateTime.UtcNow).ToString("yyyy-MM-dd"), report.SubjectId);
        var data = JsonNode.Parse(report.DataJson)!;
        Assert.Equal(9, data["total"]!.GetValue<int>());
        Assert.Equal(8, data["passed"]!.GetValue<int>()); // the total is off by ₹5,000.25
        Assert.Equal("Assistant check: 8 of 9 right", report.Title);
        Assert.All(data["questions"]!.AsArray(), q => Assert.NotNull(q!["callId"]));
        Assert.All(await ai.Db.AiCalls.ToListAsync(), c => Assert.Equal(("check", AiCatalog.AssistantCheck, AiCatalog.DeskAssistant), (c.Source, c.RequestedBy, c.AgentKey)));
    }

    [Fact]
    public async Task A_run_still_trading_is_read_again_after_the_answer()
    {
        // The first reading builds the questions; every later one sees the MCX run ₹320 lower.
        int reads = 0;
        var runs = new FakeTool(AiToolNames.Runs, _ => new
        {
            runs = new object[] { new { runId = 339, netPnl = -110132.75, charges = 59774.75 } },
            totals = new { runs = 1, netPnl = reads++ == 0 ? -162355.07 : -162675.07 },
        });
        var ai = Build(tools: [runs]);
        ai.Provider.On(Judge1,
            Answer("1 run today."),
            Answer("Together: −₹1,62,675.07 after charges."),
            Answer("Run 339."),
            Answer("−₹1,10,132.75."),
            Answer("₹59,774.75."),
            Answer("₹5,525."));

        var report = await Agent(ai).RunForAsync(null, CancellationToken.None);

        var total = JsonNode.Parse(report!.DataJson)!["questions"]!.AsArray()[1]!;
        Assert.True(total["pass"]!.GetValue<bool>());
        Assert.Equal("-162,355.07 → -162,675.07", total["expected"]!.GetValue<string>());
        Assert.Equal("Assistant check: 6 of 6 right", report.Title);
    }

    [Fact]
    public async Task A_day_below_the_pass_mark_is_marked_invalid()
    {
        var ai = Build(tools: DeskTools());
        ai.Provider.On(Judge1, Enumerable.Range(0, 9).Select(_ => Answer("I cannot tell.")).ToArray());

        var report = await Agent(ai).RunForAsync(null, CancellationToken.None);

        Assert.Equal(AiReportStatus.Invalid, report!.Status);
        Assert.Contains("Below the pass mark: 0 of 9", report.Error);
    }

    private static AssistantCheckAgent Agent(Services ai) => new(
        ai.Db, ai.Gateway, new AiReportWriter(ai.Db, ai.Options), ai.Toolbox, new AiSchedulerState(), ai.Options,
        NullLogger<AssistantCheckAgent>.Instance);

    private static IAiTool[] DeskTools() =>
    [
        new FakeTool(AiToolNames.Runs, _ => new
        {
            runs = new object[]
            {
                new { runId = 339, netPnl = -110132.75, charges = 59774.75 },
                new { runId = 341, netPnl = 5132.50, charges = 1200.00 },
            },
            totals = new { runs = 2, netPnl = -105000.25 },
        }),
        new FakeTool(AiToolNames.Checkup, _ => new { verdict = "action" }),
        new FakeTool(AiToolNames.OptionChain, _ => new { putCallRatio = 0.93, maxPain = 25000 }),
    ];
}
