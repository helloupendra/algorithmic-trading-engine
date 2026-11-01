using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Api.Services.Portfolio;
using AlgoTrading.Contracts.MutualFunds;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.MutualFunds;

/// <summary>The model behind the fund summary, so the guards are tested with answers written for the test.</summary>
public interface IMutualFundSummaryModel
{
    Task<PortfolioModelAnswer> AskAsync(long userId, IReadOnlyList<AiMessage> messages, CancellationToken cancellationToken);
}

/// <summary>
/// The fund report's one written paragraph: a model describes the report, and code decides whether anyone sees it.
/// </summary>
/// <remarks>
/// <para>
/// The same two checks as the Portfolio page's summary (<see cref="PortfolioSummaryWriter"/>): every number, date and
/// time must be one the report's facts hold (<see cref="AiFactCheck"/>), and no advice wording may appear
/// (<see cref="PortfolioAdviceGuard"/>, plus the rating words a fund description invites: best, worst, top, star,
/// rating, outperform, underperform, safe, guaranteed). The scheme's, the AMC's, the category's and the benchmark's
/// names are taken out before the wording check, so "Target Maturity" or "Opportunities Fund" in a name is not read as
/// advice. A paragraph that fails is sent back once with what failed; a second failure leaves the report without a
/// summary, and the page says why. The report is complete without it.
/// </para>
/// <para>
/// A summary that passed is kept in memory for 12 hours under the scheme, the as-of day and a hash of the facts, so
/// a report viewed again costs no model call, and a report whose figures changed gets a new one.
/// </para>
/// </remarks>
public sealed partial class MutualFundSummaryWriter(IMutualFundSummaryModel model, IMemoryCache cache, TimeProvider? time = null)
{
    public const int MaxChars = 1400;

    public const string NotPassedNote =
        "The written summary was left out: it did not pass the desk's checks (a number not in the report, or advice or rating wording).";

    public const string UnavailableNote =
        "The written summary is not available this time: no model could be asked. The figures are complete without it.";

    public const string BusyNote = "The written summary is not available right now: other summaries are being written. Try again in a minute.";

    private static readonly SemaphoreSlim Writers = new(2, 2);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<MutualFundSummaryResponse> WriteAsync(long userId, MutualFundReportResponse report, CancellationToken cancellationToken)
    {
        var response = new MutualFundSummaryResponse { SchemeCode = report.Scheme.SchemeCode, AsOf = report.AsOf };
        string facts = Facts(report);
        string key = $"mf-summary:{report.Scheme.SchemeCode}:{report.AsOf:yyyy-MM-dd}:{Hash(facts)}";
        if (cache.TryGetValue(key, out MutualFundSummaryDto? cached) && cached is not null)
        {
            response.Summary = cached;
            return response;
        }

        if (!await Writers.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken))
        {
            response.Note = BusyNote;
            return response;
        }

        try
        {
            var (summary, note) = await AskAsync(userId, report, facts, cancellationToken);
            if (summary is not null) cache.Set(key, summary, TimeSpan.FromHours(12));
            response.Summary = summary;
            response.Note = note;
            return response;
        }
        finally
        {
            Writers.Release();
        }
    }

    private async Task<(MutualFundSummaryDto? Summary, string? Note)> AskAsync(long userId, MutualFundReportResponse report, string facts, CancellationToken ct)
    {
        var messages = new List<AiMessage> { new("user", "Describe this mutual fund report:\n" + facts) };
        var first = await model.AskAsync(userId, messages, ct);
        if (!first.Ok) return (null, UnavailableNote);

        var problems = Problems(first.Text, facts, Names(report));
        if (problems.Count == 0) return (Passed(first), null);

        messages.Add(new AiMessage("assistant", first.Text));
        messages.Add(new AiMessage("user",
            "That paragraph cannot be shown: " + string.Join("; ", problems) + ". Write it again with every number copied from " +
            "the report and no advice, judgement or rating: describe the scheme's figures, never what to do. Reply with the paragraph only."));
        var second = await model.AskAsync(userId, messages, ct);
        if (!second.Ok) return (null, UnavailableNote);
        return Problems(second.Text, facts, Names(report)).Count == 0 ? (Passed(second), null) : (null, NotPassedNote);
    }

    private MutualFundSummaryDto Passed(PortfolioModelAnswer answer) =>
        new() { Text = answer.Text.Trim(), Model = answer.Model, WrittenUtc = _time.GetUtcNow().UtcDateTime };

    /// <summary>Why <paramref name="text"/> cannot be shown; empty when it can.</summary>
    public static IReadOnlyList<string> Problems(string? text, string facts, IReadOnlyList<string> names)
    {
        string t = (text ?? string.Empty).Trim();
        if (t.Length == 0) return ["it is empty"];
        var problems = new List<string>();
        if (t.Length > MaxChars) problems.Add($"it is longer than {MaxChars} characters");

        string words = names.Where(n => n.Length > 0).OrderByDescending(n => n.Length)
            .Aggregate(t, (acc, name) => acc.Replace(name, " ", StringComparison.OrdinalIgnoreCase));
        var advice = PortfolioAdviceGuard.Find(words).Concat(RatingWords().Matches(words).Select(m => m.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (advice.Count > 0) problems.Add("it gives advice or a rating (" + string.Join(", ", advice.Select(a => $"\"{a}\"")) + ")");

        var unsupported = AiFactCheck.Unsupported(t, facts);
        if (unsupported.Count > 0) problems.Add("these numbers are not in the report: " + string.Join(", ", unsupported));
        return problems;
    }

    /// <summary>The names a paragraph may quote whatever words they contain.</summary>
    public static IReadOnlyList<string> Names(MutualFundReportResponse report) =>
        [report.Scheme.SchemeName, report.Scheme.Amc, report.CategoryName, report.Benchmark.Name ?? string.Empty, report.Category.PeerGroup ?? string.Empty];

    /// <summary>
    /// The facts the model is handed and its numbers are checked against: the scheme, its returns and worked examples,
    /// its falls, the benchmark and category comparisons, the cost, and the data notes. Percentages to two decimals,
    /// rupees to the rupee, dates as "30 Oct 2026".
    /// </summary>
    public static string Facts(MutualFundReportResponse r)
    {
        static string D(DateOnly? d) => d is { } day ? day.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : null!;
        static double? P(double? pct) => pct is { } v ? Math.Round(v, 2, MidpointRounding.AwayFromZero) : null;
        static double? Rs(double? amount) => amount is { } v ? Math.Round(v, 0, MidpointRounding.AwayFromZero) : null;

        var facts = new
        {
            scheme = new { name = r.Scheme.SchemeName, amc = r.Scheme.Amc, plan = r.Scheme.Plan, option = r.Scheme.Option, category = r.CategoryName, type = r.SchemeType },
            asOf = D(r.AsOf),
            nav = Math.Round(r.Nav, 4),
            history = new { from = D(r.History.FirstDate), to = D(r.History.LastDate), complete = r.History.Complete },
            annualisedReturnsPct = r.Returns.Select(p => new
            {
                years = p.Years,
                from = D(p.Start),
                fund = P(p.CagrPct),
                benchmark = P(p.BenchmarkCagrPct),
                differencePoints = P(p.ExcessPct),
                notCovered = p.CagrPct is null ? p.Missing : null,
            }).ToList(),
            worked = r.Examples.Where(e => e.Sip is not null).Select(e => new
            {
                years = e.Years,
                sip = new { monthly = e.Sip!.Instalment, instalments = e.Sip.Instalments, from = D(e.Sip.FirstDate), invested = Rs(e.Sip.Invested), value = Rs(e.Sip.Value), xirrPct = P(e.Sip.XirrPct) },
                lumpSum = e.LumpSum is null ? null : new { amount = e.LumpSum.Amount, from = D(e.LumpSum.Start), value = Rs(e.LumpSum.Value), annualisedPct = P(e.LumpSum.CagrPct) },
            }).ToList(),
            falls = r.Risk.Drawdowns.Select(d => new
            {
                window = d.Years is { } y ? $"{y} years" : "all stored history",
                fallPct = P(d.MaxDrawdownPct),
                peak = D(d.Peak),
                trough = D(d.Trough),
                recovered = D(d.Recovery),
            }).ToList(),
            volatility = r.Risk.Volatility is { } v ? new { years = v.Years, annualisedPct = P(v.AnnualisedPct) } : null,
            benchmark = r.Benchmark.Name is null ? null : new
            {
                name = r.Benchmark.Name,
                basis = r.Benchmark.Note,
                beta = r.Benchmark.Beta is { } b ? Math.Round(b.Beta, 2) : (double?)null,
                trackingErrorPct = P(r.Benchmark.TrackingError?.ValuePct),
            },
            category = r.Category.Periods.Count == 0 ? null : new
            {
                peers = r.Category.PeerGroup,
                asOf = D(r.Category.AsOf),
                places = r.Category.Periods.Where(p => p.Rank is not null).Select(p => new { years = p.Years, rank = p.Rank, of = p.Peers, medianPct = P(p.MedianPct) }).ToList(),
            },
            expenseRatio = r.Cost.Ter is { } ter ? new { date = D(ter.Date), regularPct = ter.RegularPct, directPct = ter.DirectPct } : null,
            directVsRegular = r.Cost.PlanGap.Where(g => g.SipGap is not null)
                .Select(g => new { years = g.Years, sipDirect = Rs(g.SipDirect), sipRegular = Rs(g.SipRegular), lumpSumDirect = Rs(g.LumpSumDirect), lumpSumRegular = Rs(g.LumpSumRegular) })
                .ToList(),
            dataNotes = r.Notes,
        };

        return JsonSerializer.Serialize(facts, Json);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    [GeneratedRegex(@"\b(best|worst|top|top[-\s]?pick|stars?|rat(ing|ed|ings)|outperform\w*|underperform\w*|safe(st|r)?|guarantee\w*|winners?|must[-\s]have)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RatingWords();
}

/// <summary>
/// The fund summary's model, through the desk's AI gateway: the analyst tier's chain with only its Amazon Bedrock
/// models. Bedrock only, because NVIDIA's free tier is for development and research, and serving a platform user is
/// production use (<see cref="AiCatalog.ProviderTerms"/>). With no Bedrock model switched on there is no summary.
/// </summary>
public sealed class GatewayMutualFundSummaryModel(AiGateway gateway, TradingDbContext db, IOptionsMonitor<AiSettings> settings) : IMutualFundSummaryModel
{
    public async Task<PortfolioModelAnswer> AskAsync(long userId, IReadOnlyList<AiMessage> messages, CancellationToken cancellationToken)
    {
        var state = await new AiSettingsStore(db, null, settings).LoadAsync(cancellationToken);
        var chain = state.Tier("analyst").Chain.Where(AiCatalog.OnBedrock).ToList();
        if (chain.Count == 0) return new PortfolioModelAnswer(false, string.Empty, string.Empty, "No Amazon Bedrock model is switched on.");

        string user = userId.ToString(CultureInfo.InvariantCulture);
        var result = await gateway.AskAsync(new AiAskInput(
            AiCatalog.FundAnalyst, null, messages, AiCatalog.FundAnalystPrompt, 2000, 0.2,
            $"fund-{user}", "mutual-funds", $"fund:{user}", userId, Chain: chain),
            NullAiStreamSink.Instance, cancellationToken);

        return result.Outcome == AiCallOutcome.Ok && !string.IsNullOrWhiteSpace(result.Text)
            ? new PortfolioModelAnswer(true, result.Text, result.Model, null)
            : new PortfolioModelAnswer(false, string.Empty, result.Model, result.Error);
    }
}
