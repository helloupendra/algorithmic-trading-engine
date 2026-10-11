using System.Globalization;
using AlgoTrading.Api.Services.AiVoice;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Api.Services.AiActions;

/// <summary>
/// Every word an action says, written by code from what it resolved: the summary the owner confirms, the line asking
/// for his PIN, and the result. No model writes any of it, so the owner confirms exactly what will run.
/// </summary>
public static class IraActionText
{
    /// <summary>How a proposal is refused while the owner has no PIN.</summary>
    public const string NoPin = "Set a PIN first: /pin 1234 on Telegram or the Action PIN box on AI → Assistant.";

    /// <summary>What the bot asks for after a proposal on Telegram.</summary>
    public const string TypePin = "Type your PIN to confirm, or /cancel.";

    public const string PinUsage = "A PIN is 4 to 8 digits. Set it with /pin 1234; change it with /pin <old> <new>. I delete the message.";

    /// <summary>Who an action runs as, in the run's history and the activity log: "upendra via Ira".</summary>
    public static string Actor(string userName) => $"{userName} via Ira";

    /// <summary>An instant as the owner reads it: "11:42 IST".</summary>
    public static string Ist(DateTime utc) => IstTime.ToIst(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToString("HH:mm", CultureInfo.InvariantCulture) + " IST";

    /// <summary>A strategy as it is said: "GhostTangentCrossings" is "Ghost Tangent Crossings".</summary>
    public static string Strategy(string? name) => IraAlertText.Name(name);

    /// <summary>A P&amp;L with its sign: "−₹1,240", "₹5,068", "₹0".</summary>
    public static string Net(decimal value)
    {
        decimal whole = Math.Round(value, 0, MidpointRounding.AwayFromZero);
        return (whole < 0 ? "−" : string.Empty) + IraAlertText.Rupees(whole);
    }

    public static string StopSummary(IraStopRun p) =>
        $"Stop run {p.RunId} ({Strategy(p.Strategy)} on {p.Underlying}, {p.Account}'s account) and square off its open positions, as the Stop button does.";

    public static string StartSummary(IraStartRun p) =>
        $"Start {Strategy(p.Strategy)} on {p.Underlying} with {Lots(p.Lots)} in {p.Account}'s account (paper), through the Start button's checks.";

    public static string SquareOffSummary(IraSquareOff p, IReadOnlyList<string> legs)
    {
        string which = p.All ? $"all {Count(p.PositionIds.Count, "open position")}" : Count(p.PositionIds.Count, "position");
        string listed = string.Join("; ", legs.Take(MaxLegs));
        if (legs.Count > MaxLegs) listed += $"; and {legs.Count - MaxLegs} more";
        return $"Square off {which} in {p.Account}'s manual book at the market: {listed}.";
    }

    public static string SwitchSummary(IraAgentSwitch p) => p.Agent == AiCatalog.AiTrader && !p.On
        ? "Pause the AI Trader: switch it off, so it takes no new look and places nothing until it is switched on again. Its open positions stay as they are."
        : $"Switch the {p.AgentName} {(p.On ? "on" : "off")}, as AI → Agents does.";

    /// <summary>The line a stop ends with: "Done: run 512 (Ghost, NIFTY, admin) stopped at 11:42 IST; net −₹1,240 after charges."</summary>
    public static string Stopped(IraStopRun p, DateTime atUtc, int flattened, decimal? net)
    {
        string squared = flattened switch
        {
            0 => string.Empty,
            _ => $", {Count(flattened, "position")} squared off",
        };
        string tail = net is decimal n ? $"; net {Net(n)} after charges" : string.Empty;
        return $"Done: run {p.RunId} ({Strategy(p.Strategy)}, {p.Underlying}, {p.Account}) stopped at {Ist(atUtc)}{squared}{tail}.";
    }

    public static string Started(IraStartRun p, long runId, DateTime atUtc) =>
        $"Done: run {runId} started at {Ist(atUtc)}: {Strategy(p.Strategy)} on {p.Underlying}, {Lots(p.Lots)}, in {p.Account}'s account.";

    public static string SquaredOff(IraSquareOff p, int closed, int asked, DateTime atUtc, IReadOnlyList<string> symbols, decimal realized)
    {
        string what = string.Join(", ", symbols.Take(MaxLegs)) + (symbols.Count > MaxLegs ? $" and {symbols.Count - MaxLegs} more" : string.Empty);
        string skipped = closed < asked ? $" ({asked - closed} had closed already)" : string.Empty;
        return $"Done: squared off {Count(closed, "position")} in {p.Account}'s manual book at {Ist(atUtc)}{skipped}: {what}; realized {Net(realized)} before charges.";
    }

    public static string Switched(IraAgentSwitch p, DateTime atUtc) =>
        $"Done: the {p.AgentName} is switched {(p.On ? "on" : "off")} at {Ist(atUtc)}.";

    /// <summary>A refusal from the desk, as the owner reads it: "Not done: …".</summary>
    public static string NotDone(string why) => $"Not done: {why.Trim().TrimEnd('.')}.";

    /// <summary>The Telegram message after a proposal: the summary, its expiry and how to confirm.</summary>
    public static string AskPin(AiAction action) =>
        $"Action #{action.Id}: {action.Summary}\nIt waits until {Ist(action.ExpiresUtc)}. {TypePin}";

    public static string Locked(DateTime untilUtc) =>
        $"Actions are locked until {Ist(untilUtc)} after {IraPinGuard.MaxWrong} wrong PINs.";

    public static string WrongPin(int left, DateTime? lockedUntilUtc) => lockedUntilUtc is DateTime until
        ? $"Wrong PIN. {Locked(until)} Nothing was done."
        : $"Wrong PIN: {left} {(left == 1 ? "try" : "tries")} left before actions lock for {IraPinGuard.LockFor.TotalMinutes:0} minutes. Nothing was done.";

    public static string Expired(AiAction action) =>
        $"Action #{action.Id} expired at {Ist(action.ExpiresUtc)}: nothing was done. Ask again.";

    public static string Cancelled(AiAction action) => $"Cancelled action #{action.Id}: nothing was done.";

    /// <summary>Why an action that is no longer waiting cannot be confirmed or cancelled.</summary>
    public static string NotWaiting(AiAction action) => action.Status switch
    {
        AiActionStatus.Executed => $"Action #{action.Id} was already carried out.",
        AiActionStatus.Confirmed => $"Action #{action.Id} is already being carried out.",
        AiActionStatus.Failed => $"Action #{action.Id} was already tried: {action.Error}",
        AiActionStatus.Expired => Expired(action),
        AiActionStatus.Cancelled => $"Action #{action.Id} was cancelled: nothing was done.",
        _ => $"Action #{action.Id} is {action.Status}.",
    };

    private const int MaxLegs = 8;

    public static string Lots(int lots) => lots == 1 ? "1 lot" : $"{lots} lots";

    public static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    /// <summary>"Two", "Three"… for a count a sentence starts with; digits past nine.</summary>
    public static string Number(int n) => n switch
    {
        2 => "Two", 3 => "Three", 4 => "Four", 5 => "Five", 6 => "Six", 7 => "Seven", 8 => "Eight", 9 => "Nine",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };
}
