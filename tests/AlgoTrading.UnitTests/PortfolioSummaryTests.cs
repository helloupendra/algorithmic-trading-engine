using AlgoTrading.Api.Services.Portfolio;
using AlgoTrading.Contracts.Portfolio;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Portfolio;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The written summary is facts only: every number must be the report's, no advice in any language, one repair, and
/// a user's holdings are never kept where an admin could read them.
/// </summary>
public class PortfolioSummaryTests
{
    // ---------- the advice guard ----------

    [Theory]
    [InlineData("You may consider selling some of the IT stocks.")]
    [InlineData("Ye stock kharid lo, abhi sasta hai.")]
    [InlineData("Is share ko bech do.")]
    [InlineData("Profit book karo aur nikal jao.")]
    [InlineData("Aapko ye rakhna chahiye.")]
    [InlineData("इसे अभी खरीद लें।")]
    [InlineData("Set a stop-loss at 2,400 and a target of 2,800.")]
    [InlineData("It would be wise to rebalance towards banks.")]
    [InlineData("Book profits in RELIANCE and accumulate TCS.")]
    [InlineData("Hold TCS for the long term.")]
    [InlineData("The portfolio holds 12 stocks.")]
    [InlineData("You should avoid small caps.")]
    public void Advice_is_refused_in_english_hindi_and_hinglish(string text)
    {
        Assert.True(PortfolioAdviceGuard.IsAdvice(text), $"not caught: {text}");
    }

    [Theory]
    [InlineData("The largest holding is 34% of value.")]
    [InlineData("The portfolio has 12 holdings worth ₹4,52,300, and its five largest holdings are 71% of it.")]
    [InlineData("Over 250 sessions its annual volatility was 18.4% and its beta against NIFTY 50 was 1.12; the largest fall was 22.5%.")]
    [InlineData("Sector is unknown for every holding, so no sector tilt can be described.")]
    public void Facts_pass_the_advice_guard(string text)
    {
        Assert.Empty(PortfolioAdviceGuard.Find(text));
    }

    [Fact]
    public void Consider_selling_is_named_word_by_word()
    {
        Assert.Equal(["consider", "selling"], PortfolioAdviceGuard.Find("You may consider selling."));
        Assert.Equal(["kharid"], PortfolioAdviceGuard.Find("kharid lo"));
    }

    // ---------- the writer ----------

    [Fact]
    public async Task A_paragraph_whose_numbers_are_all_the_report_s_is_shown()
    {
        var report = Report();
        var model = new ScriptedModel("The portfolio is worth ₹1,00,000 against ₹87,000 invested, a gain of ₹13,000 (14.94%). Its largest holding is 40% of value and the five largest are 95%.");

        var outcome = await new PortfolioSummaryWriter(model).WriteAsync(7, report, CancellationToken.None);

        Assert.NotNull(outcome.Summary);
        Assert.True(outcome.Summary!.Checked);
        Assert.Equal("test-model", outcome.Summary.Model);
        Assert.Single(model.Asked);
    }

    [Fact]
    public async Task A_paragraph_with_advice_is_sent_back_once_and_the_repair_is_shown_when_it_passes()
    {
        var model = new ScriptedModel(
            "The portfolio is worth ₹1,00,000; consider trimming the largest holding at 40%.",
            "The portfolio is worth ₹1,00,000. Its largest holding is 40% of value.");

        var outcome = await new PortfolioSummaryWriter(model).WriteAsync(7, Report(), CancellationToken.None);

        Assert.Equal("The portfolio is worth ₹1,00,000. Its largest holding is 40% of value.", outcome.Summary!.Text);
        Assert.Equal(2, model.Asked.Count);
        // The repair names what failed, and carries the first answer back.
        var repair = model.Asked[1];
        Assert.Equal(["user", "assistant", "user"], repair.Select(m => m.Role));
        Assert.Contains("\"consider\"", repair[2].Content);
    }

    [Fact]
    public async Task A_number_the_report_does_not_hold_fails_and_two_failures_leave_no_summary()
    {
        var model = new ScriptedModel(
            "The portfolio is worth about ₹1.2 lakh, up 15.5%.",
            "The portfolio gained ₹14,200 this year.");

        var outcome = await new PortfolioSummaryWriter(model).WriteAsync(7, Report(), CancellationToken.None);

        Assert.Null(outcome.Summary);
        Assert.Equal(PortfolioSummaryWriter.NotPassedNote, outcome.Note);
        Assert.Contains("1.2", model.Asked[1][2].Content);
    }

    [Fact]
    public async Task No_model_to_ask_leaves_the_report_without_a_summary_and_says_so()
    {
        var outcome = await new PortfolioSummaryWriter(new ScriptedModel()).WriteAsync(7, Report(), CancellationToken.None);

        Assert.Null(outcome.Summary);
        Assert.Equal(PortfolioSummaryWriter.UnavailableNote, outcome.Note);
    }

    [Fact]
    public void A_cached_summary_is_shown_only_while_the_report_still_says_what_it_says()
    {
        var summary = new PortfolioSummary("The portfolio is worth ₹1,00,000.", "m", true, DateTime.UtcNow);

        Assert.True(PortfolioSummaryWriter.StillHolds(summary, Report()));
        Assert.False(PortfolioSummaryWriter.StillHolds(summary, Report(firstValue: 50000)));
    }

    [Fact]
    public void The_prompt_asks_for_owns_and_invested_in_and_never_hold()
    {
        Assert.Contains("\"the portfolio owns 12 stocks\"", AiCatalog.PortfolioAnalystPrompt);
        Assert.Contains("is invested in", AiCatalog.PortfolioAnalystPrompt);
        Assert.Contains("never write \"hold\", \"holds\"", AiCatalog.PortfolioAnalystPrompt);
        // And the guard refuses the word whatever the prompt says.
        Assert.Equal(["hold"], PortfolioAdviceGuard.Find("You hold 12 stocks."));
    }

    [Fact]
    public void The_model_is_given_weights_as_percents_while_the_api_sends_fractions()
    {
        var report = Report();
        string facts = PortfolioSummaryWriter.Facts(report);

        Assert.Equal(0.4m, report.Concentration.Top1Weight);
        Assert.Contains("\"largestInvestmentPct\":40", facts);
        Assert.Contains("\"top5Pct\":95", facts);
    }

    [Fact]
    public void The_portfolio_analyst_is_named_on_its_calls_but_is_not_an_agent_on_the_ai_page()
    {
        Assert.Equal("Portfolio Analyst", AiCatalog.AgentName(AiCatalog.PortfolioAnalyst));
        Assert.DoesNotContain(AiCatalog.Agents, a => a.Key == AiCatalog.PortfolioAnalyst);
        Assert.Contains("never tell the person what to do", AiCatalog.PortfolioAnalystPrompt);
    }

    // ---------- helpers ----------

    /// <summary>A report worth ₹1,00,000 on ₹87,000 invested, its largest holding 40% and the top five 95%.</summary>
    private static PortfolioReport Report(decimal firstValue = 40000) => PortfolioReportBuilder.Build(new PortfolioReportInputs(
        new DateTime(2026, 10, 16, 10, 0, 0, DateTimeKind.Utc),
        [PortfolioReportTests.Snapshot("fyers",
            PortfolioReportTests.Holding("NSE:AAA-EQ", "INE000000001", 1, 30000, firstValue),
            PortfolioReportTests.Holding("NSE:BBB-EQ", "INE000000002", 1, 20000, 20000),
            PortfolioReportTests.Holding("NSE:CCC-EQ", "INE000000003", 1, 10000, 15000),
            PortfolioReportTests.Holding("NSE:DDD-EQ", "INE000000004", 1, 12000, 10000),
            PortfolioReportTests.Holding("NSE:EEE-EQ", "INE000000005", 1, 10000, 10000),
            PortfolioReportTests.Holding("NSE:FFF-EQ", "INE000000006", 1, 5000, 5000))],
        new Dictionary<string, IReadOnlyList<DailyClose>>(), []));

    /// <summary>Answers in order; with nothing left to answer, it is a model that could not be asked.</summary>
    private sealed class ScriptedModel(params string[] answers) : IPortfolioSummaryModel
    {
        private readonly Queue<string> _answers = new(answers);

        public List<IReadOnlyList<AiMessage>> Asked { get; } = [];

        public Task<PortfolioModelAnswer> AskAsync(long userId, IReadOnlyList<AiMessage> messages, CancellationToken cancellationToken)
        {
            Asked.Add(messages.ToList());
            return Task.FromResult(_answers.Count > 0
                ? new PortfolioModelAnswer(true, _answers.Dequeue(), "test-model", null)
                : new PortfolioModelAnswer(false, string.Empty, string.Empty, "No Amazon Bedrock model is switched on."));
        }
    }
}
