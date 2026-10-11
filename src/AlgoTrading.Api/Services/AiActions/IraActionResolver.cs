using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.AiActions;

/// <summary>A resolved proposal, ready to store: its kind, its exact target as JSON, and the sentence the owner confirms.</summary>
public sealed record IraProposal(string Kind, string ParamsJson, string Summary);

/// <summary>
/// Turns what the model asked for into the exact thing that would run, or refuses in words the model reads and relays
/// (<see cref="AiToolArgumentException"/>). Nothing is guessed: two runs that match are an error naming both, an
/// unknown account is an error naming the accounts, and a target already in the asked state is refused before the
/// owner is asked for his PIN.
/// </summary>
public sealed partial class IraActionResolver(
    TradingDbContext db,
    StrategyCatalogService catalog,
    StrategyProcessRegistry registry,
    AiSettingsStore store,
    IMarketSessionService? sessions = null,
    TimeProvider? time = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The most lots a proposed start may ask for: a guard against a model's slip, not a desk rule.</summary>
    public const int MaxLots = 50;

    private const int MaxListed = 12;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private sealed record OpenRun(long RunId, string Strategy, string Underlying, long UserId, string Account, string Status);

    // ---------- stop a run ----------

    public async Task<IraProposal> StopRunAsync(long? runId, string? strategy, string? underlying, string? account, CancellationToken cancellationToken)
    {
        OpenRun target;
        if (runId is long id)
        {
            var row = await db.SimulationRuns.AsNoTracking()
                .Where(r => r.Id == id)
                .Select(r => new { r.Id, r.Mode, r.Status, r.StrategyName, r.Symbol, r.ParametersJson, r.UserId })
                .FirstOrDefaultAsync(cancellationToken)
                ?? throw new AiToolArgumentException($"There is no run {id}.");
            string owner = await AccountNameAsync(row.UserId, cancellationToken);
            if (row.Mode != StrategyRunControl.LivePaperMode) throw new AiToolArgumentException($"Run {id} is not a live strategy run, so it cannot be stopped here.");
            if (row.StrategyName == ManualOrdersController.BookStrategyName)
            {
                throw new AiToolArgumentException($"Run {id} is {owner}'s manual book, which is never stopped: square off its positions with propose_square_off instead.");
            }

            string u = UnderlyingOf(row.ParametersJson, row.Symbol);
            if (row.Status == StrategyRunControl.RunStatusStopping) throw new AiToolArgumentException($"Run {id} ({IraActionText.Strategy(row.StrategyName)} on {u}) is already stopping.");
            if (!StrategyRunControl.IsOpenStatus(row.Status))
            {
                throw new AiToolArgumentException($"Run {id} ({IraActionText.Strategy(row.StrategyName)} on {u}, {owner}) is not running: its status is {row.Status}.");
            }

            target = new OpenRun(row.Id, row.StrategyName, u, row.UserId, owner, row.Status);
        }
        else
        {
            if (strategy is null && underlying is null && account is null)
            {
                throw new AiToolArgumentException("Name the run: its run_id, or its strategy, underlying and account.");
            }

            var open = (await OpenRunsAsync(cancellationToken)).Where(r => r.Status == StrategyRunControl.RunStatusRunning).ToList();
            var matches = open
                .Where(r => strategy is null || NameMatches(r.Strategy, strategy))
                .Where(r => underlying is null || string.Equals(r.Underlying, underlying.Trim(), StringComparison.OrdinalIgnoreCase))
                .Where(r => account is null || string.Equals(r.Account, account.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            string asked = Describe(strategy, underlying, account);
            if (matches.Count == 0)
            {
                throw new AiToolArgumentException(open.Count == 0
                    ? "No strategy run is live now: there is nothing to stop."
                    : $"No live run is {asked}. The live runs: {List(open)}.");
            }

            if (matches.Count > 1) throw new AiToolArgumentException(Ambiguous(matches) + " Which one? Give its run_id.");
            target = matches[0];
        }

        var p = new IraStopRun(target.RunId, target.Strategy, target.Underlying, target.UserId, target.Account);
        return new IraProposal(AiActionKind.StopRun, JsonSerializer.Serialize(p, Json), IraActionText.StopSummary(p));
    }

    // ---------- start a run ----------

    public async Task<IraProposal> StartRunAsync(IraOwner owner, string strategy, string underlying, int lots, string? account, CancellationToken cancellationToken)
    {
        var entries = await catalog.GetAllAsync(cancellationToken);
        var exact = entries.Where(e => Norm(e.Name) == Norm(strategy)).ToList();
        var matches = exact.Count > 0 ? exact : entries.Where(e => NameMatches(e.Name, strategy)).ToList();
        if (matches.Count == 0)
        {
            throw new AiToolArgumentException($"No strategy is named {strategy}. The strategies: {string.Join(", ", entries.Select(e => e.Name).OrderBy(n => n).Take(40))}.");
        }

        if (matches.Count > 1)
        {
            throw new AiToolArgumentException($"{IraActionText.Number(matches.Count)} strategies match {strategy}: {string.Join(", ", matches.Select(e => e.Name).OrderBy(n => n))}. Which one?");
        }

        var entry = matches[0];
        if (!string.IsNullOrWhiteSpace(entry.Error)) throw new AiToolArgumentException($"{entry.Name} cannot be started: {entry.Error}");

        string u = underlying.Trim().ToUpperInvariant();
        if (!UnderlyingShape().IsMatch(u)) throw new AiToolArgumentException($"{underlying} is not an underlying (NIFTY, BANKNIFTY, SENSEX, CRUDEOIL…).");
        if (lots is < 1 or > MaxLots) throw new AiToolArgumentException($"lots must be 1 to {MaxLots}.");

        var (accountId, accountName) = await AccountAsync(account, owner, cancellationToken);
        if (registry.Find(entry.Id, u, accountId) is { } running)
        {
            throw new AiToolArgumentException($"{IraActionText.Strategy(entry.Name)} is already running on {u} in {accountName}'s account as run {running.RunId}.");
        }

        // The Start button's market-hours check, asked now so the owner is not asked for a PIN to be refused; the
        // start itself asks again, with every other check, when it runs.
        if (sessions is not null && StrategyStartService.WhyMarketClosed(sessions, _time.GetUtcNow().UtcDateTime, u, UnderlyingCatalog.SpotSymbolFor(u)) is string closed)
        {
            throw new AiToolArgumentException(closed);
        }

        var p = new IraStartRun(entry.Id, entry.Name, u, lots, accountId, accountName);
        return new IraProposal(AiActionKind.StartRun, JsonSerializer.Serialize(p, Json), IraActionText.StartSummary(p));
    }

    // ---------- square off ----------

    public async Task<IraProposal> SquareOffAsync(IraOwner owner, long? positionId, string? symbol, bool all, string? account, CancellationToken cancellationToken)
    {
        var (accountId, accountName) = await AccountAsync(account, owner, cancellationToken);
        var book = await ManualBook.FindAsync(db, accountId, cancellationToken)
            ?? throw new AiToolArgumentException($"{accountName} has no open manual book: nothing to square off.");
        var open = await db.PaperPositions.AsNoTracking()
            .Where(p => p.SimulationRunId == book.Id && p.Status == "Open")
            .OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.Symbol, p.Direction, p.Quantity, p.AveragePrice })
            .ToListAsync(cancellationToken);
        if (open.Count == 0) throw new AiToolArgumentException($"{accountName}'s manual book has no open position: nothing to square off.");

        string Leg(long id, string sym, string dir, int qty, decimal avg) =>
            $"#{id} {sym} {dir.ToUpperInvariant()} {qty.ToString(CultureInfo.InvariantCulture)} at {IraAlertTextPrice(avg)}";
        string Listed() => string.Join("; ", open.Take(MaxListed).Select(p => Leg(p.Id, p.Symbol, p.Direction, p.Quantity, p.AveragePrice)))
                           + (open.Count > MaxListed ? $"; and {open.Count - MaxListed} more" : string.Empty);

        var chosen = open;
        if (positionId is long id)
        {
            chosen = open.Where(p => p.Id == id).ToList();
            if (chosen.Count == 0)
            {
                var elsewhere = await db.PaperPositions.AsNoTracking()
                    .Where(p => p.Id == id)
                    .Select(p => new { p.SimulationRunId, p.Status })
                    .FirstOrDefaultAsync(cancellationToken);
                throw new AiToolArgumentException(elsewhere is null
                    ? $"There is no position {id}. {accountName}'s open manual positions: {Listed()}."
                    : elsewhere.SimulationRunId != book.Id
                        ? $"Position {id} is not in {accountName}'s manual book (it belongs to run {elsewhere.SimulationRunId}): a strategy run's legs are squared off by stopping the run."
                        : $"Position {id} is not open (it is {elsewhere.Status}). {accountName}'s open manual positions: {Listed()}.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(symbol))
        {
            string wanted = Norm(symbol);
            chosen = open.Where(p => Norm(p.Symbol).Contains(wanted, StringComparison.Ordinal)).ToList();
            if (chosen.Count == 0) throw new AiToolArgumentException($"No open manual position of {accountName} is {symbol}. Open: {Listed()}.");
            if (chosen.Count > 1 && !all)
            {
                throw new AiToolArgumentException(
                    $"{IraActionText.Number(chosen.Count)} open positions match {symbol}: {string.Join("; ", chosen.Select(p => Leg(p.Id, p.Symbol, p.Direction, p.Quantity, p.AveragePrice)))}. Which one? Give its position_id, or set all to true for every one of them.");
            }
        }
        else if (!all)
        {
            throw new AiToolArgumentException($"Name the position (position_id or symbol), or set all to true for every open position in {accountName}'s book: {Listed()}.");
        }

        bool everything = chosen.Count == open.Count && all;
        var p = new IraSquareOff(book.Id, accountId, accountName, chosen.Select(c => c.Id).ToList(), everything);
        var legs = chosen.Select(c => Leg(c.Id, c.Symbol, c.Direction, c.Quantity, c.AveragePrice)).ToList();
        return new IraProposal(AiActionKind.SquareOff, JsonSerializer.Serialize(p, Json), IraActionText.SquareOffSummary(p, legs));
    }

    // ---------- an agent's switch ----------

    public async Task<IraProposal> AgentSwitchAsync(string agent, bool on, string? reason, CancellationToken cancellationToken)
    {
        var def = FindAgent(agent);
        if (def.Key == AiCatalog.DeskAssistant)
        {
            throw new AiToolArgumentException("The Desk Assistant is the one answering: it is not switched from a chat with itself. Use AI → Agents on the console.");
        }

        if (!def.Built) throw new AiToolArgumentException($"The {def.Name} is not built yet: there is nothing to switch.");
        var state = await store.LoadAsync(cancellationToken);
        if (state.Agent(def.Key) is { } now && now.Enabled == on)
        {
            throw new AiToolArgumentException($"The {def.Name} is already {(on ? "on" : "off")}: nothing to change.");
        }

        string? why = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var p = new IraAgentSwitch(def.Key, def.Name, on, why);
        return new IraProposal(AiActionKind.AgentSwitch, JsonSerializer.Serialize(p, Json), IraActionText.SwitchSummary(p));
    }

    /// <summary>The agent the model named, by key ("ai-trader"), name ("AI Trader") or a distinctive part of either.</summary>
    public static AiAgentDef FindAgent(string asked)
    {
        string wanted = Norm(asked);
        if (wanted.Length == 0) throw new AiToolArgumentException("Name the agent.");
        var exact = AiCatalog.Agents.Where(a => Norm(a.Key) == wanted || Norm(a.Name) == wanted).ToList();
        var matches = exact.Count > 0 ? exact : AiCatalog.Agents.Where(a => Norm(a.Name).Contains(wanted, StringComparison.Ordinal)).ToList();
        string known = string.Join(", ", AiCatalog.Agents.Where(a => a.Built && a.Key != AiCatalog.DeskAssistant).Select(a => $"{a.Key} ({a.Name})"));
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new AiToolArgumentException($"No agent is named {asked}. The agents: {known}."),
            _ => throw new AiToolArgumentException($"{IraActionText.Number(matches.Count)} agents match {asked}: {string.Join(", ", matches.Select(a => $"{a.Key} ({a.Name})"))}. Which one?"),
        };
    }

    // ---------- helpers ----------

    /// <summary>The live paper strategy runs still open, with their accounts and underlyings: never a manual book.</summary>
    private async Task<IReadOnlyList<OpenRun>> OpenRunsAsync(CancellationToken cancellationToken)
    {
        var rows = await db.SimulationRuns.AsNoTracking()
            .Where(r => r.Mode == StrategyRunControl.LivePaperMode
                        && r.StrategyName != ManualOrdersController.BookStrategyName
                        && (r.Status == StrategyRunControl.RunStatusRunning || r.Status == StrategyRunControl.RunStatusStopping))
            .OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.StrategyName, r.Symbol, r.ParametersJson, r.UserId, r.Status })
            .ToListAsync(cancellationToken);
        var ids = rows.Select(r => r.UserId).Distinct().ToList();
        var names = await db.AppUsers.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.UserName, cancellationToken);
        return rows.Select(r => new OpenRun(r.Id, r.StrategyName, UnderlyingOf(r.ParametersJson, r.Symbol), r.UserId,
            names.GetValueOrDefault(r.UserId) ?? $"user {r.UserId}", r.Status)).ToList();
    }

    /// <summary>The account the model named (an active user), or the owner's own when it named none.</summary>
    private async Task<(long Id, string Name)> AccountAsync(string? account, IraOwner owner, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account) || string.Equals(account.Trim(), owner.UserName, StringComparison.OrdinalIgnoreCase))
        {
            return (owner.UserId, owner.UserName);
        }

        string wanted = account.Trim().ToLowerInvariant();
        var user = await db.AppUsers.AsNoTracking()
            .Where(u => u.UserName.ToLower() == wanted)
            .Select(u => new { u.Id, u.UserName, u.IsActive })
            .FirstOrDefaultAsync(cancellationToken);
        if (user is null || !user.IsActive)
        {
            var active = await db.AppUsers.AsNoTracking().Where(u => u.IsActive && u.Role != Domain.Constants.UserRoles.Service)
                .OrderBy(u => u.UserName).Select(u => u.UserName).Take(30).ToListAsync(cancellationToken);
            throw new AiToolArgumentException(user is null
                ? $"There is no account named {account}. The accounts: {string.Join(", ", active)}."
                : $"{user.UserName}'s account is disabled.");
        }

        return (user.Id, user.UserName);
    }

    private async Task<string> AccountNameAsync(long userId, CancellationToken cancellationToken) =>
        await db.AppUsers.AsNoTracking().Where(u => u.Id == userId).Select(u => u.UserName).FirstOrDefaultAsync(cancellationToken) ?? $"user {userId}";

    private static string UnderlyingOf(string? parametersJson, string? symbol) =>
        LiveRunParameters.Parse(parametersJson).Underlying
        ?? UnderlyingCatalog.UnderlyingForSpot(symbol)
        ?? UnderlyingCatalog.InferUnderlying(symbol);

    /// <summary>"Two live Ghost Tangent Crossings runs on NIFTY: run 512 (admin) and run 530 (coderforchange)."</summary>
    private static string Ambiguous(IReadOnlyList<OpenRun> matches)
    {
        bool oneStrategy = matches.Select(m => m.Strategy).Distinct(StringComparer.Ordinal).Count() == 1;
        bool oneUnderlying = matches.Select(m => m.Underlying).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
        string head = oneStrategy && oneUnderlying
            ? $"{IraActionText.Number(matches.Count)} live {IraActionText.Strategy(matches[0].Strategy)} runs on {matches[0].Underlying}"
            : $"{IraActionText.Number(matches.Count)} live runs match";
        var each = matches.Take(MaxListed).Select(m => oneStrategy && oneUnderlying
            ? $"run {m.RunId} ({m.Account})"
            : $"run {m.RunId} ({IraActionText.Strategy(m.Strategy)} on {m.Underlying}, {m.Account})").ToList();
        string listed = each.Count == 2 ? $"{each[0]} and {each[1]}" : string.Join(", ", each);
        if (matches.Count > MaxListed) listed += $" and {matches.Count - MaxListed} more";
        return $"{head}: {listed}.";
    }

    private static string List(IReadOnlyList<OpenRun> runs) =>
        string.Join(", ", runs.Take(MaxListed).Select(r => $"run {r.RunId} ({IraActionText.Strategy(r.Strategy)} on {r.Underlying}, {r.Account})"))
        + (runs.Count > MaxListed ? $" and {runs.Count - MaxListed} more" : string.Empty);

    private static string Describe(string? strategy, string? underlying, string? account)
    {
        var parts = new List<string>();
        if (strategy is not null) parts.Add(strategy.Trim());
        if (underlying is not null) parts.Add($"on {underlying.Trim().ToUpperInvariant()}");
        if (account is not null) parts.Add($"in {account.Trim()}'s account");
        return string.Join(' ', parts);
    }

    /// <summary>Whether a strategy's name is the one asked for: equal, or containing it, case and spacing aside ("ghost" is Ghost Tangent Crossings).</summary>
    public static bool NameMatches(string name, string asked)
    {
        string a = Norm(asked);
        return a.Length > 0 && (Norm(name) == a || (a.Length >= 3 && Norm(name).Contains(a, StringComparison.Ordinal)));
    }

    private static string Norm(string? text) => NotWord().Replace((text ?? string.Empty).ToLowerInvariant(), string.Empty);

    private static string IraAlertTextPrice(decimal value) => AiVoice.IraAlertText.Rupees(value);

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NotWord();

    [GeneratedRegex("^[A-Z0-9&_-]{2,20}$")]
    private static partial Regex UnderlyingShape();
}
