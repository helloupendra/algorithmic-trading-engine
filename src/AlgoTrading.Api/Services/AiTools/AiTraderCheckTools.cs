using System.Globalization;
using System.Text.Json.Nodes;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using static AlgoTrading.Api.Services.AiTools.AiToolFormat;

namespace AlgoTrading.Api.Services.AiTools;

/// <summary>
/// <c>get_ai_trader_checks</c>: the Validator's verdicts, the Risk Memo's memos and the Execution Reporter's notes on the
/// AI Trader's decisions and fills, read when the owner talks with one of the three. Read-only, like every chat tool.
/// </summary>
public sealed class AiTraderChecksTool(TradingDbContext db, TimeProvider? time = null) : IAiTool
{
    private const int DefaultTake = 30;
    private const int MaxTake = 60;
    private static readonly string[] Agents = [AiCatalog.Validator, AiCatalog.RiskMemo, AiCatalog.ExecutionReporter];
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => AiCatalog.AiTraderChecksTool;

    public string Description =>
        "The checks on the AI Trader's live work, newest first: the Validator's verdicts and the Risk Memo's memos on its " +
        "decisions (subject: the decision's id), and the Execution Reporter's notes on its fills (subject: the paper " +
        "order's id). One IST day's (day), or one decision's (decisionId: its verdict and memo, and the notes on the fills " +
        "it made). Each: the agent, the subject, the status (ok; invalid: the answer was not in the asked shape; failed: " +
        "no answer), the title, and the structured part (a verdict with its samples; a memo with what was applied and " +
        "ignored; a fill's slippage and timing). Filter by agent.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("day", AiToolSchema.Text("IST day as yyyy-MM-dd, or 'today' / 'yesterday'. Default today."), false),
        ("decisionId", AiToolSchema.Integer("One AI Trader decision: its verdict, its memo and the notes on its fills."), false),
        ("agent", AiToolSchema.OneOf("Whose records. Default all three.", "any", AiCatalog.Validator, AiCatalog.RiskMemo, AiCatalog.ExecutionReporter), false),
        ("take", AiToolSchema.Integer($"How many to list, 1 to {MaxTake}. Default {DefaultTake}."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        var day = args.Date("day", IstTime.DateOf(_time.GetUtcNow().UtcDateTime)) ?? IstTime.DateOf(_time.GetUtcNow().UtcDateTime);
        long? decisionId = args.Long("decisionId", 1);
        string agent = args.String("agent", 24)?.ToLowerInvariant() ?? "any";
        if (agent != "any" && !Agents.Contains(agent)) throw new AiToolArgumentException("agent is any, validator, risk-memo or execution-reporter.");
        int take = args.Int("take", 1, MaxTake) ?? DefaultTake;

        var query = db.AiReports.AsNoTracking().Where(r => Agents.Contains(r.AgentKey));
        if (agent != "any") query = query.Where(r => r.AgentKey == agent);
        if (decisionId is long id)
        {
            string subject = id.ToString(CultureInfo.InvariantCulture);
            var fills = await DecisionOrdersAsync(id, cancellationToken);
            query = query.Where(r => (r.SubjectType == AiReportSubject.Decision && r.SubjectId == subject)
                                     || (r.SubjectType == AiReportSubject.Order && fills.Contains(r.SubjectId)));
        }
        else
        {
            query = query.Where(r => r.SessionDate == day);
        }

        var rows = await query.OrderByDescending(r => r.Id).Take(take).ToListAsync(cancellationToken);
        var data = new
        {
            day = decisionId is null ? day.ToString("yyyy-MM-dd") : null,
            decisionId,
            records = rows.Select(r => new
            {
                reportId = r.Id,
                agent = r.AgentKey,
                subject = $"{r.SubjectType} {r.SubjectId}",
                status = r.Status,
                title = Text(r.Title, 300),
                note = Text(r.Body, 1200),
                data = AiTraderToolText.Node(r.DataJson),
                error = Text(r.Error, 300),
                written = Ist(r.UpdatedUtc),
            }).ToList(),
            totals = new
            {
                validator = rows.Count(r => r.AgentKey == AiCatalog.Validator),
                riskMemo = rows.Count(r => r.AgentKey == AiCatalog.RiskMemo),
                fills = rows.Count(r => r.AgentKey == AiCatalog.ExecutionReporter),
            },
        };

        return new AiToolOutput(data, rows.Count == 0 ? null : rows.Max(r => r.UpdatedUtc), rows.Count,
            $"{rows.Count} records" + (decisionId is long d ? $" on decision {d}" : $" ({day:dd MMM})"));
    }

    /// <summary>The paper orders a decision placed, as report subjects: its buy's order, or its exit's.</summary>
    private async Task<List<string>> DecisionOrdersAsync(long decisionId, CancellationToken cancellationToken)
    {
        var resultJson = await db.AiTraderDecisions.AsNoTracking().Where(d => d.Id == decisionId).Select(d => d.ResultJson).FirstOrDefaultAsync(cancellationToken);
        var placement = AiTrader.AiTraderResults.Placement(resultJson);
        return placement?.OrderId is long order ? [order.ToString(CultureInfo.InvariantCulture)] : [];
    }
}
