using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Contracts.Portfolio;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.Portfolio;

/// <summary>What a model answered for the summary: its text and which model wrote it, or why there is none.</summary>
public sealed record PortfolioModelAnswer(bool Ok, string Text, string Model, string? Error);

/// <summary>The model behind the summary, so the guards are tested with answers written for the test.</summary>
public interface IPortfolioSummaryModel
{
    Task<PortfolioModelAnswer> AskAsync(long userId, IReadOnlyList<AiMessage> messages, CancellationToken cancellationToken);
}

/// <summary>A summary that passed both checks, or the plain reason the report has none.</summary>
public sealed record PortfolioSummaryOutcome(PortfolioSummary? Summary, string? Note);

/// <summary>
/// The Portfolio page's one written paragraph: a model describes the report, and code decides whether a user ever
/// sees what it wrote.
/// </summary>
/// <remarks>
/// <para>
/// The model is handed the report's facts (<see cref="Facts"/>), not the broker answers. Two checks stand between its
/// paragraph and the page: every number, date and time in it must be one the facts hold (<see cref="AiFactCheck"/>,
/// the same check the desk analysts pass), and no word of advice may appear (<see cref="PortfolioAdviceGuard"/>).
/// An answer that fails is sent back once with what failed; a second failure leaves the report without a summary
/// and a data note saying so. The page works the same either way.
/// </para>
/// <para>
/// The summary is written at a sync and cached on the snapshot. Each view checks it again against the report shown
/// (<see cref="StillHolds"/>), so a figure that changed since cannot be shown beside a paragraph that says otherwise.
/// </para>
/// </remarks>
public sealed class PortfolioSummaryWriter(IPortfolioSummaryModel model, TimeProvider? time = null)
{
    public const int MaxChars = 1200;

    public const string NotPassedNote =
        "The written summary was left out: it did not pass the desk's checks (a number not in the report, or advice wording).";

    public const string UnavailableNote = "The written summary is not available this time: no model could be asked. The figures above are complete without it.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<PortfolioSummaryOutcome> WriteAsync(long userId, PortfolioReport report, CancellationToken cancellationToken)
    {
        string facts = Facts(report);
        var messages = new List<AiMessage> { new("user", "Describe this portfolio report:\n" + facts) };

        var first = await model.AskAsync(userId, messages, cancellationToken);
        if (!first.Ok) return new PortfolioSummaryOutcome(null, UnavailableNote);

        var problems = Problems(first.Text, facts);
        if (problems.Count == 0) return Passed(first);

        // One repair: the paragraph back, with exactly what failed.
        messages.Add(new AiMessage("assistant", first.Text));
        messages.Add(new AiMessage("user",
            "That paragraph cannot be shown: " + string.Join("; ", problems) + ". Write it again with every number copied from " +
            "the report and no advice: describe the portfolio, never what to do with it. Reply with the paragraph only."));
        var second = await model.AskAsync(userId, messages, cancellationToken);
        if (!second.Ok) return new PortfolioSummaryOutcome(null, UnavailableNote);

        return Problems(second.Text, facts).Count == 0 ? Passed(second) : new PortfolioSummaryOutcome(null, NotPassedNote);
    }

    /// <summary>Whether a cached summary still says only what <paramref name="report"/> says.</summary>
    public static bool StillHolds(PortfolioSummary summary, PortfolioReport report) => Problems(summary.Text, Facts(report)).Count == 0;

    /// <summary>Why <paramref name="text"/> cannot be shown; empty when it can.</summary>
    public static IReadOnlyList<string> Problems(string? text, string facts)
    {
        var problems = new List<string>();
        string t = (text ?? string.Empty).Trim();
        if (t.Length == 0) return ["it is empty"];
        if (t.Length > MaxChars) problems.Add($"it is longer than {MaxChars} characters");

        var advice = PortfolioAdviceGuard.Find(t);
        if (advice.Count > 0) problems.Add("it gives advice (" + string.Join(", ", advice.Select(a => $"\"{a}\"")) + ")");

        var unsupported = AiFactCheck.Unsupported(t, facts);
        if (unsupported.Count > 0) problems.Add("these numbers are not in the report: " + string.Join(", ", unsupported));

        return problems;
    }

    /// <summary>
    /// The facts the model is handed and its numbers are checked against: totals, the largest holdings, concentration,
    /// what is known of sectors and market caps, the risk figures, derivatives, cash, and the data notes.
    /// </summary>
    public static string Facts(PortfolioReport report)
    {
        bool KnownSlices(IReadOnlyList<PortfolioSlice> slices) => slices.Any(s => s.Name != PortfolioReportBuilder.UnknownClass);
        var facts = new
        {
            asOf = $"{IstTime.ToIst(report.AsOfUtc):yyyy-MM-dd HH:mm} IST",
            brokers = report.Sources.Select(s => PortfolioBrokers.NameOf(s.Broker)).Distinct().ToList(),
            totals = new
            {
                currentValue = report.Totals.Current,
                invested = report.Totals.Invested,
                pnl = report.Totals.Pnl,
                pnlPct = report.Totals.PnlPct,
                dayChange = report.Totals.DayChange,
                dayChangePct = report.Totals.DayChangePct,
                stocks = report.Totals.HoldingsCount,
            },
            largestInvestments = report.Holdings.Where(h => h.Value is not null).Take(10).Select(h => new
            {
                symbol = h.Symbol,
                name = h.Name,
                weightPct = Pct(h.Weight),
                value = h.Value,
                pnl = h.Pnl,
                pnlPct = h.PnlPct,
                sector = h.Sector == PortfolioReportBuilder.UnknownClass ? null : h.Sector,
            }).ToList(),
            concentration = new
            {
                largestInvestmentPct = Pct(report.Concentration.Top1Weight),
                top5Pct = Pct(report.Concentration.Top5Weight),
                hhi = report.Concentration.Hhi,
            },
            sectors = KnownSlices(report.Allocation.BySector)
                ? report.Allocation.BySector.Select(s => new { name = s.Name, weightPct = Pct(s.Weight) }).ToList()
                : null,
            marketCapBands = KnownSlices(report.Allocation.ByMarketCap)
                ? report.Allocation.ByMarketCap.Select(s => new { name = s.Name, weightPct = Pct(s.Weight) }).ToList()
                : null,
            byBroker = report.Allocation.ByBroker.Select(s => new { name = s.Name, weightPct = Pct(s.Weight) }).ToList(),
            risk = new
            {
                benchmark = PortfolioRiskMath.Benchmark,
                tradingDays = report.Risk.LookbackDays,
                annualVolatilityPct = report.Risk.VolatilityAnnualPct,
                betaVsBenchmark = report.Risk.BetaVsNifty,
                maxDrawdownPct = report.Risk.MaxDrawdownPct,
                coveredWeightPct = Pct(report.Risk.CoveredWeight),
                note = report.Risk.Note,
            },
            derivatives = new { openPositions = report.Derivatives.OpenPositions, grossExposure = report.Derivatives.GrossExposure },
            cashAvailable = report.Funds.Where(f => f.Available is not null)
                .Select(f => new { broker = PortfolioBrokers.NameOf(f.Broker), available = f.Available }).ToList(),
            dataNotes = report.DataNotes,
        };

        return JsonSerializer.Serialize(facts, Json);
    }

    /// <summary>
    /// A weight as the model is given it: a percentage. The API sends weights as fractions; a person writes "40%", and the
    /// number check compares what is written with what was given.
    /// </summary>
    private static decimal? Pct(decimal? fraction) => fraction is decimal f ? Math.Round(f * 100, 2, MidpointRounding.AwayFromZero) : null;

    private PortfolioSummaryOutcome Passed(PortfolioModelAnswer answer) =>
        new(new PortfolioSummary(answer.Text.Trim(), answer.Model, Checked: true, _time.GetUtcNow().UtcDateTime), null);
}

/// <summary>
/// The summary's model, through the desk's AI gateway: the analyst tier's chain with only its Amazon Bedrock models,
/// as a private call.
/// </summary>
/// <remarks>
/// Bedrock only, because NVIDIA's free tier is for development and research, and serving a platform user is
/// production use (<see cref="AiCatalog.ProviderTerms"/>). With no Bedrock model switched on there is no summary, and
/// the report says so. Private (<see cref="AiAskInput.Private"/>), so the Calls tab keeps the call's cost and timing
/// but not the user's holdings.
/// </remarks>
public sealed class GatewayPortfolioSummaryModel(AiGateway gateway, TradingDbContext db, IOptionsMonitor<AiSettings> settings) : IPortfolioSummaryModel
{
    public async Task<PortfolioModelAnswer> AskAsync(long userId, IReadOnlyList<AiMessage> messages, CancellationToken cancellationToken)
    {
        var state = await new AiSettingsStore(db, null, settings).LoadAsync(cancellationToken);
        var chain = state.Tier("analyst").Chain.Where(AiCatalog.OnBedrock).ToList();
        if (chain.Count == 0) return new PortfolioModelAnswer(false, string.Empty, string.Empty, "No Amazon Bedrock model is switched on.");

        string user = userId.ToString(CultureInfo.InvariantCulture);
        var result = await gateway.AskAsync(new AiAskInput(
            AiCatalog.PortfolioAnalyst, null, messages, AiCatalog.PortfolioAnalystPrompt, 2000, 0.2,
            $"portfolio-{user}", "portfolio", $"portfolio:{user}", userId, Chain: chain, Private: true),
            NullAiStreamSink.Instance, cancellationToken);

        return result.Outcome == AiCallOutcome.Ok && !string.IsNullOrWhiteSpace(result.Text)
            ? new PortfolioModelAnswer(true, result.Text, result.Model, null)
            : new PortfolioModelAnswer(false, string.Empty, result.Model, result.Error);
    }
}
