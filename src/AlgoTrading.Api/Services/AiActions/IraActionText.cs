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

    // ---------- closing an account ----------

    /// <summary>How many runs, positions or failures a sentence names before "and N more"; the action keeps them all.</summary>
    public const int MaxCloseListed = 6;

    /// <summary>A run as the close lists it: "Ghost Tangent Crossings NIFTY run 512".</summary>
    public static string RunLabel(IraCloseRun r) => $"{Strategy(r.Strategy)} {r.Underlying} run {r.RunId}";

    /// <summary>A manual position as the close lists it: "NIFTY 25100 CE · 27 Oct ×2" (lots).</summary>
    public static string PositionLabel(IraClosePosition p) => $"{p.Contract} ×{p.Lots.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// What the owner confirms: "Close everything in admin's account: stop 7 runs (…) and square off their 9 open
    /// positions, and square off 3 manual positions (…). Net now: runs −₹12,340 after charges, manual positions
    /// −₹1,200 unrealized."
    /// </summary>
    public static string CloseSummary(IraCloseAccount p)
    {
        var parts = new List<string>();
        if (p.Runs.Count > 0)
        {
            int legs = p.Runs.Sum(r => r.OpenLegs);
            string runs = $"stop {Count(p.Runs.Count, "run")} ({Listed(p.Runs.Select(RunLabel).ToList())})";
            runs += legs == 0
                ? p.Runs.Count == 1 ? ", which holds no open position" : ", which hold no open position"
                : $" and square off {(p.Runs.Count == 1 ? "its" : "their")} {Count(legs, "open position")}";
            parts.Add(runs);
        }

        if (p.Positions.Count > 0) parts.Add($"square off {Count(p.Positions.Count, "manual position")} ({Listed(p.Positions.Select(PositionLabel).ToList())})");

        decimal? runsNet = p.Runs.Count > 0 && p.Runs.All(r => r.Net is not null) ? p.Runs.Sum(r => r.Net!.Value) : null;
        decimal? manual = p.Positions.Count > 0 && p.Positions.All(x => x.Unrealized is not null) ? p.Positions.Sum(x => x.Unrealized!.Value) : null;
        string net = (runsNet, manual) switch
        {
            (decimal r, decimal m) => $" Net now: runs {Net(r)} after charges, manual positions {Net(m)} unrealized.",
            (decimal r, null) when p.Positions.Count == 0 => $" Net now {Net(r)} after charges.",
            (decimal r, null) => $" Net now: runs {Net(r)} after charges.",
            (null, decimal m) when p.Runs.Count == 0 => $" Unrealized now {Net(m)}, before charges.",
            (null, decimal m) => $" Net now: manual positions {Net(m)} unrealized.",
            _ => string.Empty,
        };
        return $"Close everything in {p.Account}'s account: {string.Join(", and ", parts)}.{net}";
    }

    /// <summary>
    /// The one reply to an account close: "Done: admin's account closed at 12:41 IST: 7 runs stopped, 9 strategy
    /// positions and 3 manual positions squared off. Day net −₹12,880 after charges." With a failure it says "Partly
    /// done" and names it; what had closed already is named too.
    /// </summary>
    public static string AccountClosed(string account, DateTime atUtc, IReadOnlyList<IraCloseItem> items, decimal? realized, decimal? dayNet)
    {
        var done = items.Where(i => i.State == IraCloseState.Done).ToList();
        var skipped = items.Where(i => i.State == IraCloseState.AlreadyClosed).ToList();
        var failed = items.Where(i => i.State == IraCloseState.Failed).ToList();
        int stopped = done.Count(i => i.Type == IraCloseItem.RunType);
        int flattened = done.Where(i => i.Type == IraCloseItem.RunType).Sum(i => i.Flattened);
        int squared = done.Count(i => i.Type == IraCloseItem.PositionType);

        var squaredOff = new List<string>();
        if (flattened > 0) squaredOff.Add(Count(flattened, "strategy position"));
        if (squared > 0) squaredOff.Add(Count(squared, "manual position"));
        // The day net is Today's, which counts the runs: what the manual positions realized is said on its own.
        string manualRealized = squared > 0 && realized is decimal r
            ? flattened > 0 ? $" (the manual ones realized {Net(r)} before charges)" : $" (realized {Net(r)} before charges)"
            : string.Empty;
        var did = new List<string>();
        if (stopped > 0) did.Add($"{Count(stopped, "run")} stopped");
        if (squaredOff.Count > 0) did.Add($"{string.Join(" and ", squaredOff)} squared off{manualRealized}");

        string failures = failed.Count == 0 ? string.Empty
            : $"{failed.Count.ToString(CultureInfo.InvariantCulture)} failed: {Listed(failed.Select(f => $"{f.Label} ({Cut(f.Reason ?? "no reason given", 160)})").ToList())}";
        string closedAlready = skipped.Count == 0 ? string.Empty
            : $"{skipped.Count.ToString(CultureInfo.InvariantCulture)} had closed already: {Listed(skipped.Select(s => s.Label).ToList())}";
        string tail = dayNet is decimal d ? $" Day net {Net(d)} after charges." : string.Empty;
        string at = Ist(atUtc);

        if (done.Count == 0 && failed.Count == 0)
        {
            return $"Not done: everything in {account}'s account had closed already ({Listed(skipped.Select(s => s.Label).ToList())}): nothing was left to close.{tail}";
        }

        if (done.Count == 0) return $"Not done: nothing in {account}'s account was closed at {at}; {Joined(failures, closedAlready)}.{tail}";

        string what = string.Join(", ", did);
        return failed.Count == 0
            ? $"Done: {account}'s account closed at {at}: {what}{(closedAlready.Length > 0 ? "; " + closedAlready : string.Empty)}.{tail}"
            : $"Partly done: {account}'s account at {at}: {what}; {Joined(failures, closedAlready)}.{tail}";
    }

    /// <summary>One item's activity-log sentence: "Closing admin's account (action #12) via Ira: run 512 (Ghost Tangent Crossings on NIFTY) stopped, 2 positions squared off; net −₹1,240 after charges."</summary>
    public static string CloseItemLine(AiAction action, string account, IraCloseItem item)
    {
        string head = $"Closing {account}'s account (action #{action.Id}) via Ira ({action.Channel}): {item.Label} ({item.Detail})";
        return item.State switch
        {
            IraCloseState.Done when item.Type == IraCloseItem.RunType =>
                $"{head} stopped{(item.Flattened > 0 ? $", {Count(item.Flattened, "position")} squared off" : string.Empty)}{(item.Net is decimal n ? $"; net {Net(n)} after charges" : string.Empty)}.",
            IraCloseState.Done => $"{head} squared off.",
            IraCloseState.AlreadyClosed => $"{head}: nothing to do, {item.Reason ?? "it had closed already"}.",
            _ => $"{head} not closed: {item.Reason ?? "no reason given"}.",
        };
    }

    /// <summary>"a", "a and b", "a, b and c"; past <see cref="MaxCloseListed"/>, "a, b, … f and N more".</summary>
    private static string Listed(IReadOnlyList<string> items)
    {
        if (items.Count == 0) return string.Empty;
        if (items.Count > MaxCloseListed) return $"{string.Join(", ", items.Take(MaxCloseListed))} and {items.Count - MaxCloseListed} more";
        return items.Count == 1 ? items[0] : $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}";
    }

    private static string Joined(string first, string second) =>
        first.Length == 0 ? second : second.Length == 0 ? first : $"{first}; {second}";

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

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
