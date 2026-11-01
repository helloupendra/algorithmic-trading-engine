using AlgoTrading.Api.Services.MutualFunds;
using AlgoTrading.Api.Services.Portfolio;
using AlgoTrading.Contracts.MutualFunds;
using AlgoTrading.Infrastructure.Ai;
using Microsoft.Extensions.Caching.Memory;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The fund report's written summary is facts only: every number must be the report's, no advice or rating in any
/// language, one repair, and the page works without it.
/// </summary>
public class MutualFundSummaryTests
{
    private static MutualFundReportResponse Report() => new()
    {
        Scheme = new MutualFundSchemeDto { SchemeCode = 120716, SchemeName = "UTI Nifty 50 Index Fund", Amc = "UTI Mutual Fund", Plan = "direct", Option = "growth" },
        CategoryName = "Other Scheme - Index Funds",
        AsOf = new DateOnly(2026, 10, 30),
        Nav = 162.3751,
        History = new MutualFundHistoryDto { FirstDate = new DateOnly(2013, 1, 2), LastDate = new DateOnly(2026, 10, 30), Complete = true },
        Returns =
        [
            new MutualFundPeriodDto { Years = 1, Start = new DateOnly(2025, 10, 30), End = new DateOnly(2026, 10, 30), CagrPct = -10.158513758533894, BenchmarkCagrPct = -9.994806718493321, ExcessPct = -0.16370704 },
            new MutualFundPeriodDto { Years = 5, Start = new DateOnly(2021, 11, 1), End = new DateOnly(2026, 10, 30), CagrPct = 6.155138359454515 },
        ],
        Examples =
        [
            new MutualFundExampleDto
            {
                Years = 5,
                Sip = new MutualFundSipDto { Instalment = 10_000, Instalments = 60, FirstDate = new DateOnly(2021, 11, 1), Invested = 600_000, Value = 679_412.41, XirrPct = 4.920914 },
                LumpSum = new MutualFundLumpSumDto { Amount = 200_000, Start = new DateOnly(2021, 11, 1), Value = 269_565.33, CagrPct = 6.155138 },
            },
        ],
        Risk = new MutualFundRiskDto
        {
            Drawdowns = [new MutualFundDrawdownDto { Years = 5, MaxDrawdownPct = -15.9087, Peak = new DateOnly(2022, 1, 17), Trough = new DateOnly(2022, 6, 17), Recovery = new DateOnly(2022, 11, 1) }],
        },
        Benchmark = new MutualFundBenchmarkDto { Index = "NIFTY 50", Name = "NIFTY 50 TRI", Basis = "index-name", Note = "NIFTY 50 TRI: the index the scheme's name says it tracks." },
        Cost = new MutualFundCostDto { Ter = new MutualFundTerDto { Date = new DateOnly(2026, 10, 30), RegularPct = 0.37, DirectPct = 0.25 } },
    };

    private const string Good =
        "UTI Nifty 50 Index Fund (direct plan) returned -10.16% over the year to 30 Oct 2026, against -9.99% for NIFTY 50 TRI, " +
        "and 6.16% a year over 5 years. ₹10,000 a month from 1 Nov 2021, 60 instalments and ₹6,00,000 in all, was worth ₹6,79,412 " +
        "on 30 Oct 2026, an XIRR of 4.92%; ₹2,00,000 invested on 1 Nov 2021 was worth ₹2,69,565. Its deepest fall over 5 years " +
        "was 15.91%, from 17 Jan 2022 to 17 Jun 2022, made good by 1 Nov 2022. Its direct-plan TER was 0.25%.";

    [Fact]
    public void A_paragraph_of_the_report_s_own_numbers_passes()
    {
        var report = Report();
        Assert.Empty(MutualFundSummaryWriter.Problems(Good, MutualFundSummaryWriter.Facts(report), MutualFundSummaryWriter.Names(report)));
    }

    [Theory]
    [InlineData("It is the best index fund in its category.", "best")]
    [InlineData("A top performer with a five-star rating.", "top")]
    [InlineData("Investors should consider adding it.", "should")]
    [InlineData("It outperformed most peers.", "outperformed")]
    [InlineData("Ye fund kharid lo.", "kharid")]
    [InlineData("A safe choice for the long term.", "safe")]
    [InlineData("Hold it through the cycle.", "Hold")]
    public void Advice_or_rating_words_fail(string sentence, string word)
    {
        var report = Report();
        var problems = MutualFundSummaryWriter.Problems(sentence, MutualFundSummaryWriter.Facts(report), MutualFundSummaryWriter.Names(report));
        Assert.Contains(problems, p => p.Contains($"\"{word}\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Words_inside_the_scheme_s_own_name_are_not_advice()
    {
        var report = Report();
        report.Scheme.SchemeName = "Bharat Bond ETF Target Maturity April 2030 Opportunities Fund";
        string text = "Bharat Bond ETF Target Maturity April 2030 Opportunities Fund returned 6.16% a year over 5 years.";
        Assert.Empty(MutualFundSummaryWriter.Problems(text, MutualFundSummaryWriter.Facts(report), MutualFundSummaryWriter.Names(report)));
    }

    [Fact]
    public void A_number_the_report_does_not_hold_fails()
    {
        var report = Report();
        var problems = MutualFundSummaryWriter.Problems("It returned 12.4% a year over 5 years, ₹7,10,000 on the SIP.",
            MutualFundSummaryWriter.Facts(report), MutualFundSummaryWriter.Names(report));
        Assert.Contains(problems, p => p.Contains("12.4", StringComparison.Ordinal) && p.Contains("7,10,000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_paragraph_is_sent_back_once_then_left_out()
    {
        var model = new ScriptedModel("The best fund to buy now.", "Still the top choice: 99% of peers trail it.");
        var writer = new MutualFundSummaryWriter(model, new MemoryCache(new MemoryCacheOptions()));

        var result = await writer.WriteAsync(7, Report(), default);

        Assert.Null(result.Summary);
        Assert.Equal(MutualFundSummaryWriter.NotPassedNote, result.Note);
        Assert.Equal(2, model.Asked.Count);
        Assert.Contains("cannot be shown", model.Asked[1][^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repaired_paragraph_is_shown_and_kept_for_the_next_view()
    {
        var model = new ScriptedModel("The best index fund around.", Good);
        var writer = new MutualFundSummaryWriter(model, new MemoryCache(new MemoryCacheOptions()));

        var first = await writer.WriteAsync(7, Report(), default);
        var second = await writer.WriteAsync(8, Report(), default);

        Assert.Equal(Good, first.Summary!.Text);
        Assert.Equal("test-model", first.Summary.Model);
        Assert.Equal(Good, second.Summary!.Text);
        Assert.Equal(2, model.Asked.Count);   // the second view cost no model call
        Assert.Equal((120716, new DateOnly(2026, 10, 30)), (first.SchemeCode, first.AsOf));
    }

    [Fact]
    public async Task Without_a_model_the_report_has_no_summary_and_says_why()
    {
        var result = await new MutualFundSummaryWriter(new ScriptedModel(), new MemoryCache(new MemoryCacheOptions())).WriteAsync(7, Report(), default);
        Assert.Null(result.Summary);
        Assert.Equal(MutualFundSummaryWriter.UnavailableNote, result.Note);
    }

    [Fact]
    public void The_fund_analyst_is_named_on_calls_but_is_not_one_of_the_desk_s_agents()
    {
        Assert.Equal("Fund Analyst", AiCatalog.AgentName(AiCatalog.FundAnalyst));
        Assert.DoesNotContain(AiCatalog.Agents, a => a.Key == AiCatalog.FundAnalyst);
    }

    private sealed class ScriptedModel(params string[] answers) : IMutualFundSummaryModel
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
