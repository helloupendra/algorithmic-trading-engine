using System.Text.Json.Nodes;
using AlgoTrading.Api.Services.AiActions;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;

namespace AlgoTrading.Api.Services.AiTools;

/// <summary>
/// What every proposal tool shares: who it is for (<see cref="IraActions.ProposerAsync"/>), storing the resolved
/// proposal as a pending action, and the answer the model reads back. A proposal acts on nothing; code runs it only
/// after the owner's PIN, which the model never sees.
/// </summary>
public abstract class ProposalTool(IraActions actions, TimeProvider? time) : IAiTool
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract JsonObject Parameters { get; }

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        var owner = await actions.ProposerAsync(args.Caller, cancellationToken);
        var proposal = await ResolveAsync(owner, args, cancellationToken);
        var action = await actions.ProposeAsync(owner, args.Caller!, proposal, cancellationToken);
        bool telegram = action.Channel == AiActionChannel.Telegram;
        return new AiToolOutput(
            new
            {
                proposed = true,
                actionId = action.Id,
                kind = action.Kind,
                summary = action.Summary,
                expiresAt = IraActionText.Ist(action.ExpiresUtc),
                expiresUtc = DateTime.SpecifyKind(action.ExpiresUtc, DateTimeKind.Utc),
                confirm = telegram
                    ? "The desk now sends the owner this summary and asks him to type his PIN in this chat, or /cancel."
                    : "The owner confirms it with his PIN in the card the console shows under this answer, or cancels it there.",
                note = "Nothing has happened yet. Do not ask for the PIN, and do not say it is done.",
                // What the console's confirm card lists in full (an account close); left out for the other actions.
                items = proposal.Details,
            },
            _time.GetUtcNow().UtcDateTime,
            1,
            $"Proposed action #{action.Id}: {action.Summary}");
    }

    protected abstract Task<IraProposal> ResolveAsync(IraOwner owner, AiToolArgs args, CancellationToken cancellationToken);
}

/// <summary><c>propose_stop_run</c>: stop one live strategy run, by its id or by strategy, underlying and account.</summary>
public sealed class ProposeStopRunTool(IraActions actions, IraActionResolver resolver, TimeProvider? time = null) : ProposalTool(actions, time)
{
    public override string Name => AiToolNames.ProposeStopRun;

    public override string Description =>
        "Propose stopping one live strategy run on the paper desk, squaring off its open positions as the Desk's Stop " +
        "button does. Name it by run_id (from get_runs), or by strategy, underlying and account when exactly one live run " +
        "matches; two matches are an error naming both. Nothing happens until the owner confirms with his PIN.";

    public override JsonObject Parameters => AiToolSchema.Object(
        ("run_id", AiToolSchema.Integer("The run's id, from get_runs."), false),
        ("strategy", AiToolSchema.Text("The strategy's name, or a distinctive part of it (\"Ghost\")."), false),
        ("underlying", AiToolSchema.Text("NIFTY, BANKNIFTY, SENSEX, CRUDEOIL…"), false),
        ("account", AiToolSchema.Text("The account's user name, as get_runs shows it."), false));

    protected override Task<IraProposal> ResolveAsync(IraOwner owner, AiToolArgs args, CancellationToken cancellationToken) =>
        resolver.StopRunAsync(args.Long("run_id", 1), args.String("strategy"), args.String("underlying", 20), args.String("account"), cancellationToken);
}

/// <summary><c>propose_start_run</c>: start a strategy on an underlying with a number of lots in an account.</summary>
public sealed class ProposeStartRunTool(IraActions actions, IraActionResolver resolver, TimeProvider? time = null) : ProposalTool(actions, time)
{
    public override string Name => AiToolNames.ProposeStartRun;

    public override string Description =>
        "Propose starting a strategy run on the paper desk, as the Start button does and through all its checks (the " +
        "market's hours, a run already open, the run cap, the account's package, the server's memory). The strategy's " +
        "name is as get_strategy_spec lists it. Nothing happens until the owner confirms with his PIN.";

    public override JsonObject Parameters => AiToolSchema.Object(
        ("strategy", AiToolSchema.Text("The strategy's name, or a distinctive part of it."), true),
        ("underlying", AiToolSchema.Text("NIFTY, BANKNIFTY, SENSEX, CRUDEOIL…"), true),
        ("lots", AiToolSchema.Integer($"Lots, 1 to {IraActionResolver.MaxLots}."), true),
        ("account", AiToolSchema.Text("The account's user name; the owner's own when left out."), false));

    protected override Task<IraProposal> ResolveAsync(IraOwner owner, AiToolArgs args, CancellationToken cancellationToken)
    {
        string strategy = args.String("strategy") ?? throw new AiToolArgumentException("strategy is required.");
        string underlying = args.String("underlying", 20) ?? throw new AiToolArgumentException("underlying is required.");
        int lots = args.Int("lots", 1, IraActionResolver.MaxLots) ?? throw new AiToolArgumentException("lots is required.");
        return resolver.StartRunAsync(owner, strategy, underlying, lots, args.String("account"), cancellationToken);
    }
}

/// <summary><c>propose_square_off</c>: one manual-book position, or every open one of an account's book.</summary>
public sealed class ProposeSquareOffTool(IraActions actions, IraActionResolver resolver, TimeProvider? time = null) : ProposalTool(actions, time)
{
    public override string Name => AiToolNames.ProposeSquareOff;

    public override string Description =>
        "Propose squaring off manual-book positions on the paper desk at the market, as the book's square-off does: one " +
        "position (position_id from get_open_positions, or its symbol), or every open one of an account's manual book " +
        "(all = true). A strategy run's legs are squared off by stopping the run. Nothing happens until the owner " +
        "confirms with his PIN.";

    public override JsonObject Parameters => AiToolSchema.Object(
        ("position_id", AiToolSchema.Integer("One open position's id in the manual book."), false),
        ("symbol", AiToolSchema.Text("The contract, or a distinctive part of its symbol (\"25100CE\")."), false),
        ("all", AiToolSchema.Flag("True for every open position of the account's manual book."), false),
        ("account", AiToolSchema.Text("The account's user name; the owner's own when left out."), false));

    protected override Task<IraProposal> ResolveAsync(IraOwner owner, AiToolArgs args, CancellationToken cancellationToken) =>
        resolver.SquareOffAsync(owner, args.Long("position_id", 1), args.String("symbol", 60), args.Bool("all") ?? false, args.String("account"), cancellationToken);
}

/// <summary>
/// <c>propose_close_account</c>: close everything open in one account together, its live strategy runs and its manual
/// book's open positions, on one PIN (owner, 12 Oct 2026).
/// </summary>
public sealed class ProposeCloseAccountTool(IraActions actions, IraActionResolver resolver, TimeProvider? time = null) : ProposalTool(actions, time)
{
    public override string Name => AiToolNames.ProposeCloseAccount;

    public override string Description =>
        "Propose closing everything open in one account on the paper desk, together, on one PIN: every live strategy run " +
        "of the account is stopped (its legs squared off, as the Desk's Stop button does) and every open position of its " +
        "manual book is squared off at the market. Use it when the owner asks to close all trades or positions of an " +
        "account. Code lists what is open now; nothing open is an error. Nothing happens until the owner confirms with " +
        "his PIN.";

    public override JsonObject Parameters => AiToolSchema.Object(
        ("account", AiToolSchema.Text("The account's user name as get_runs shows it (admin, coderforchange), or \"my account\" for the owner's own."), true));

    protected override Task<IraProposal> ResolveAsync(IraOwner owner, AiToolArgs args, CancellationToken cancellationToken) =>
        resolver.CloseAccountAsync(owner,
            args.String("account", 60) ?? throw new AiToolArgumentException("account is required: the account's user name, or \"my account\" for your own."),
            cancellationToken);
}

/// <summary><c>propose_agent_switch</c>: switch an AI agent on or off, the AI Trader's pause among them.</summary>
public sealed class ProposeAgentSwitchTool(IraActions actions, IraActionResolver resolver, TimeProvider? time = null) : ProposalTool(actions, time)
{
    public override string Name => AiToolNames.ProposeAgentSwitch;

    public override string Description =>
        "Propose switching one AI agent on or off, as the switch on AI → Agents does; switching the AI Trader (ai-trader) " +
        "off pauses it. The agents: " +
        string.Join(", ", AiCatalog.Agents.Where(a => a.Built && a.Key != AiCatalog.DeskAssistant).Select(a => $"{a.Key} ({a.Name})")) +
        ". Nothing happens until the owner confirms with his PIN.";

    public override JsonObject Parameters => AiToolSchema.Object(
        ("agent", AiToolSchema.Text("The agent's key (ai-trader) or name (AI Trader)."), true),
        ("on", AiToolSchema.Flag("True to switch it on, false to switch it off."), true),
        ("reason", AiToolSchema.Text("Why, in the owner's words; shown on AI → Agents."), false));

    protected override Task<IraProposal> ResolveAsync(IraOwner owner, AiToolArgs args, CancellationToken cancellationToken)
    {
        string agent = args.String("agent", 64) ?? throw new AiToolArgumentException("agent is required.");
        bool on = args.Bool("on") ?? throw new AiToolArgumentException("on is required: true or false.");
        return resolver.AgentSwitchAsync(agent, on, args.String("reason", 200), cancellationToken);
    }
}
