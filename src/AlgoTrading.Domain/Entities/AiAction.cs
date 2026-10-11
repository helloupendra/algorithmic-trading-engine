namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One action the Desk Assistant ("Ira") proposed and the owner may confirm with his PIN: stop or start a strategy
/// run, square off manual-book positions, or switch an AI agent on or off. The model only proposes; code resolved the
/// target, wrote this row, and runs it only after the PIN, as the owner's console user.
/// </summary>
/// <remarks>
/// <para>
/// A row is written <see cref="AiActionStatus.Pending"/> by the proposal tool, with the exact target code resolved
/// (<see cref="ParamsJson"/>) and the sentence the owner confirms (<see cref="Summary"/>). It waits until
/// <see cref="ExpiresUtc"/> (<c>Ai:Actions:ConfirmSeconds</c> after it was proposed). The right PIN moves it to
/// <see cref="AiActionStatus.Confirmed"/> once, then to <see cref="AiActionStatus.Executed"/> or
/// <see cref="AiActionStatus.Failed"/> with what happened; <c>/cancel</c> or the console's Cancel, a newer proposal in
/// the same conversation, or the clock end it without running.
/// </para>
/// <para>
/// The PIN is never stored here, nor anywhere in clear text: only a salted hash, in <c>system_settings</c>.
/// </para>
/// </remarks>
public class AiAction
{
    public long Id { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>After this a pending action is refused and marked expired.</summary>
    public DateTime ExpiresUtc { get; set; }

    /// <summary>The owner's console user: who confirms it, and whose name it runs under.</summary>
    public long UserId { get; set; }

    public string UserName { get; set; } = string.Empty;

    /// <summary>One of <see cref="AiActionChannel"/>: where it was proposed, and so where it may be confirmed.</summary>
    public string Channel { get; set; } = AiActionChannel.Console;

    /// <summary>The conversation it was proposed in: the console's id, or <c>tg-&lt;chat&gt;</c> on Telegram.</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>The Telegram chat it was proposed in; null on the console.</summary>
    public long? ChatId { get; set; }

    /// <summary>The model call whose tool proposed it (<c>ai_calls</c>).</summary>
    public long? CallId { get; set; }

    /// <summary>One of <see cref="AiActionKind"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>The target as code resolved it: the run, the account, the positions, the agent.</summary>
    public string ParamsJson { get; set; } = "{}";

    /// <summary>What the owner confirms, in one sentence code wrote.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>One of <see cref="AiActionStatus"/>.</summary>
    public string Status { get; set; } = AiActionStatus.Pending;

    public DateTime? ConfirmedUtc { get; set; }

    public DateTime? ExecutedUtc { get; set; }

    /// <summary>What the execution did, as JSON (its message and the ids it touched); null until it ran.</summary>
    public string? ResultJson { get; set; }

    /// <summary>Why it failed, was cancelled or expired; empty otherwise.</summary>
    public string Error { get; set; } = string.Empty;
}

/// <summary>What an action does.</summary>
public static class AiActionKind
{
    /// <summary>Stop one live strategy run, squaring off its positions, as the Desk's Stop button does.</summary>
    public const string StopRun = "stop_run";

    /// <summary>Start a strategy run through the same checks as the Start button.</summary>
    public const string StartRun = "start_run";

    /// <summary>Square off one manual-book position, or all of an account's open ones.</summary>
    public const string SquareOff = "square_off";

    /// <summary>Switch an AI agent on or off, as AI → Agents does.</summary>
    public const string AgentSwitch = "agent_switch";

    public static readonly IReadOnlyList<string> All = [StopRun, StartRun, SquareOff, AgentSwitch];
}

/// <summary>Where an action stands.</summary>
public static class AiActionStatus
{
    /// <summary>Proposed, waiting for the owner's PIN.</summary>
    public const string Pending = "pending";

    /// <summary>The PIN was right; it is being carried out.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>Ended without running: the owner cancelled it, or a newer proposal replaced it.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Not confirmed in time.</summary>
    public const string Expired = "expired";

    /// <summary>Carried out.</summary>
    public const string Executed = "executed";

    /// <summary>Confirmed, but the desk refused it or it went wrong: nothing, or only part, was done.</summary>
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> All = [Pending, Confirmed, Cancelled, Expired, Executed, Failed];
}

/// <summary>Where an action was proposed.</summary>
public static class AiActionChannel
{
    public const string Console = "console";
    public const string Telegram = "telegram";

    public static readonly IReadOnlyList<string> All = [Console, Telegram];
}
