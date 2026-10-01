using System.Text.Json.Nodes;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The Desk Assistant's weekly exam: a frozen bank about finished days, asked
/// several times, held-out days scored apart, graded by code.
/// </summary>
public class AiAssistantExamTests
{
    private static readonly DateOnly Day = new(2026, 10, 1);

    // Sunday 4 Oct 2026, 11:00 IST: the exam's day, no session.
    private static readonly DateTime Sunday = IstTime.FromIst(new DateTime(2026, 10, 4, 11, 0, 0));

    [Fact]
    public void A_finished_day_gives_its_totals_each_account_and_three_runs()
    {
        var questions = AssistantExamAgent.DayQuestions(Day, RunsOn(net: -80604.25));

        Assert.Equal(
            new[] { "day.runs", "day.net", "day.winners", "day.worst", "day.best", "day.account", "day.account" }
                .Concat(Enumerable.Repeat(new[] { "run.net", "run.charges", "run.trades" }, 3).SelectMany(x => x)),
            questions.Select(q => q.Template));
        var worst = questions.Single(q => q.Template == "day.worst");
        Assert.Equal(("Which run had the lowest net P&L on 1 Oct 2026? Give its run id.", "345"), (worst.Text, worst.Expected));
        Assert.Equal(-80604.25, questions.Single(q => q.Template == "day.net").Number);
        Assert.Equal(new[] { "345", "347", "346" }, questions.Where(q => q.Template == "run.net").Select(q => q.Subject));
        Assert.All(questions, q => Assert.Equal(AssistantExamAgent.HeldOut("day:2026-10-01") ? AiExamSet.Holdout : AiExamSet.Practice, q.Set));
    }

    [Fact]
    public void A_day_with_no_runs_gives_no_questions_and_a_tie_gives_no_worst_run()
    {
        Assert.Empty(AssistantExamAgent.DayQuestions(Day, new JsonObject { ["runs"] = new JsonArray(), ["totals"] = new JsonObject { ["runs"] = 0 } }));

        var tie = RunsOn(net: 0, worstTwice: true);
        Assert.DoesNotContain(AssistantExamAgent.DayQuestions(Day, tie), q => q.Template == "day.worst");
    }

    [Fact]
    public void About_one_day_in_four_is_held_out_and_the_choice_never_changes()
    {
        var days = Enumerable.Range(0, 400).Select(i => new DateOnly(2026, 1, 1).AddDays(i)).ToList();
        int held = days.Count(d => AssistantExamAgent.HeldOut($"day:{d:yyyy-MM-dd}"));

        Assert.InRange(held, 70, 130);
        Assert.Equal(AssistantExamAgent.HeldOut("day:2026-10-01"), AssistantExamAgent.HeldOut("day:2026-10-01"));
    }

    [Theory]
    [InlineData("On 1 Oct 2026 the desk had 30 runs.", false)]  // "1" is the date, not the count
    [InlineData("The desk ran 1 strategy on Oct 1st.", true)]
    [InlineData("On 01/10/2026 it ran 2 runs.", false)]
    public void The_day_a_question_names_is_not_read_as_its_answer(string answer, bool pass)
    {
        var q = new AiExamQuestion { Template = "day.runs", Day = Day, Text = "How many runs on 1 Oct 2026?", Kind = "number", Expected = "1", Number = 1, Tolerance = 0 };
        Assert.Equal(pass, AssistantExamAgent.Grade(q, answer));
    }

    [Fact]
    public void Pass_k_counts_a_question_only_when_every_ask_was_right_and_leaves_out_what_cannot_be_counted()
    {
        AiExamAnswer A(string outcome) => new() { Outcome = outcome };
        var q = new AiExamQuestion();
        var sittings = new List<AssistantExamAgent.Sitting>
        {
            new(q, [A(AiExamOutcome.Right), A(AiExamOutcome.Right), A(AiExamOutcome.Right)]),
            new(q, [A(AiExamOutcome.Right), A(AiExamOutcome.Wrong), A(AiExamOutcome.Right)]),
            new(q, [A(AiExamOutcome.Right), A(AiExamOutcome.NoAnswer), A(AiExamOutcome.Right)]),
            new(q, [A(AiExamOutcome.Moved), A(AiExamOutcome.Moved), A(AiExamOutcome.Moved)]),
        };

        var score = AssistantExamAgent.ScoreOf(sittings, 3);

        Assert.Equal((4, 2, 1, 1, 1), (score.Questions, score.Scored, score.PassAll, score.SetAside, score.NoAnswer));
        Assert.Equal(0.5, score.PassK);
        Assert.Equal(7.0 / 8, score.Pass1);
    }

    [Fact]
    public async Task An_exam_asks_a_few_questions_a_minute_sets_aside_a_moved_answer_and_reports_both_sets()
    {
        double net = -80604.25;
        var runs = new FakeTool(AiToolNames.Runs, args => args.String("date") == "2026-10-01" ? RunsOn(net) : Empty());
        var ai = Build(Settings(s =>
        {
            s.ExamRepeats = 2;
            s.ExamAsksPerTick = 5;
            s.ExamLookbackDays = 3;
            // 42 asks in one test minute; on the server the exam asks 2 a minute, inside both caps.
            s.GlobalPerMinute = 1000;
            s.PerUserPer10Min = 1000;
        }), null, runs);
        // Every expected figure in one answer, so every question passes; the first ask is wrong.
        const string all = "3 runs, 80,604.25, 2 winners, run 345 and 346, 85,604.25, 5,000.00, 86,804.50, 23,541.00, 167, 1,200.25, 300.00, 1,200.00, 4, 1950, 8997, 9320, 2520, 26000";
        ai.Provider.On(Judge1, [Answer("I do not know."), .. Enumerable.Range(0, 60).Select(_ => Answer(all))]);
        var exam = Exam(ai);

        Assert.True(await exam.RunOnceAsync(Sunday, CancellationToken.None));
        Assert.Equal(22, await ai.Db.AiExamQuestions.CountAsync());   // 6 fixed + 16 about 1 Oct; 2 and 3 Oct had none
        Assert.Equal(5, await ai.Db.AiExamAnswers.CountAsync());

        net = -80000;   // the day now reads differently: its net is set aside, not failed
        for (int tick = 0; tick < 20 && !await ai.Db.AiReports.AnyAsync(); tick++)
        {
            await exam.RunOnceAsync(Sunday.AddMinutes(tick + 1), CancellationToken.None);
        }

        var report = await ai.Db.AiReports.SingleAsync();
        Assert.Equal((AiReportSubject.Exam, AiReportStatus.Ok), (report.SubjectType, report.Status));
        var data = JsonNode.Parse(report.DataJson)!;
        Assert.Equal((22, 21, 20, 1), ((int)data["all"]!["questions"]!, (int)data["all"]!["scored"]!, (int)data["all"]!["passAll"]!, (int)data["all"]!["setAside"]!));
        Assert.Equal(42, ai.Provider.Requests.Count(r => r.Model == Judge1));   // the moved question was never asked
        Assert.Equal(
            (int)data["practice"]!["questions"]! + (int)data["holdout"]!["questions"]!, (int)data["all"]!["questions"]!);
        Assert.NotNull((await ai.Db.AiExams.SingleAsync()).FinishedUtc);
        Assert.All(await ai.Db.AiCalls.ToListAsync(), c => Assert.Equal(("exam", AiCatalog.AssistantExam), (c.Source, c.RequestedBy)));

        // One exam a day: the next minute starts nothing.
        Assert.False(await exam.RunOnceAsync(Sunday.AddMinutes(30), CancellationToken.None));
    }

    [Theory]
    [InlineData(2026, 10, 4, 11, 0, true)]    // Sunday after 10:30
    [InlineData(2026, 10, 4, 10, 0, false)]   // Sunday, too early
    [InlineData(2026, 10, 5, 11, 0, false)]   // Monday
    public void The_exam_starts_on_its_day_after_its_hour(int y, int m, int d, int h, int min, bool due)
    {
        Assert.Equal(due, Exam(Build()).Due(IstTime.FromIst(new DateTime(y, m, d, h, min, 0))));
    }

    // ---------- helpers ----------

    private static AssistantExamAgent Exam(Services ai) => new(
        ai.Db, ai.Gateway, new AiReportWriter(ai.Db, ai.Options), ai.Toolbox, ai.Options, NullLogger<AssistantExamAgent>.Instance);

    private static JsonObject Empty() => new() { ["date"] = "x", ["runs"] = new JsonArray(), ["totals"] = new JsonObject { ["runs"] = 0 } };

    /// <summary>What get_runs answers for 1 Oct: three stopped runs on two accounts.</summary>
    private static JsonObject RunsOn(double net, bool worstTwice = false)
    {
        JsonObject Run(long id, string account, double runNet, double charges, int trades) => new()
        {
            ["runId"] = id, ["account"] = account, ["active"] = false, ["netPnl"] = runNet, ["charges"] = charges, ["closedTrades"] = trades,
        };

        return new JsonObject
        {
            ["date"] = "2026-10-01",
            ["runs"] = new JsonArray(
                Run(345, "admin", -86804.50, 23541, 167),
                Run(346, "coderforchange", 5000, 1200, 4),
                Run(347, "admin", worstTwice ? -86804.50 : 1200.25, 300, 2)),
            ["totals"] = new JsonObject
            {
                ["runs"] = 3, ["netPnl"] = net, ["winners"] = 2,
                ["byAccount"] = new JsonArray(
                    new JsonObject { ["account"] = "admin", ["runs"] = 2, ["netPnl"] = -85604.25 },
                    new JsonObject { ["account"] = "coderforchange", ["runs"] = 1, ["netPnl"] = 5000 }),
            },
        };
    }
}
