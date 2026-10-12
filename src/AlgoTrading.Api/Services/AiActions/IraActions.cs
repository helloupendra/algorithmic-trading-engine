using System.Globalization;
using System.Text.Json;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiActions;

/// <summary>How a confirm or a cancel ended.</summary>
public enum IraConfirmKind
{
    /// <summary>Carried out.</summary>
    Executed,

    /// <summary>Confirmed, but the desk refused it or it went wrong.</summary>
    Failed,

    /// <summary>The PIN was wrong; nothing ran.</summary>
    WrongPin,

    /// <summary>Actions are locked after three wrong PINs.</summary>
    Locked,

    /// <summary>No such action for this owner on this channel.</summary>
    NotFound,

    /// <summary>It is no longer waiting: done, cancelled, being carried out.</summary>
    NotPending,

    /// <summary>It was not confirmed in time.</summary>
    Expired,

    /// <summary>The owner has no PIN.</summary>
    NoPin,

    /// <summary>Cancelled by the owner.</summary>
    Cancelled,

    /// <summary>Actions are switched off on this desk (<c>Ai:Actions:Enabled</c>).</summary>
    Off,
}

/// <summary>What a confirm or a cancel came to, with the owner's line.</summary>
public sealed record IraConfirmResult(IraConfirmKind Kind, string Message, AiAction? Action, int AttemptsLeft = 0, DateTime? LockedUntilUtc = null);

/// <summary>How setting the PIN ended.</summary>
public enum IraPinChange
{
    Set,
    Changed,
    BadShape,

    /// <summary>A PIN is set already: changing it on Telegram needs the old one.</summary>
    NeedCurrent,
    WrongCurrent,
    Locked,
}

/// <summary>
/// Ira's actions (owner, 11 Oct 2026): the Desk Assistant proposes, code runs it after the owner's PIN.
/// </summary>
/// <remarks>
/// <para>
/// A proposal tool resolves its target (<see cref="IraActionResolver"/>) and stores it here as a pending
/// <see cref="AiAction"/>, replacing any older pending one in the same conversation, so a PIN always confirms the one
/// the owner was just shown. It waits <c>Ai:Actions:ConfirmSeconds</c>.
/// </para>
/// <para>
/// The owner confirms with his PIN: typed on Telegram (handled by the bot before any model is asked) or in the
/// console's confirm card. Three wrong PINs within ten minutes lock his actions for fifteen
/// (<see cref="IraPinGuard"/>). The right one moves the row to confirmed exactly once, under one gate, so a double
/// confirm runs nothing twice; then code carries it out as the owner (<see cref="IIraActionHands"/>), through the
/// same services and checks as his own click, and records how it ended.
/// </para>
/// <para>
/// Every outcome (executed, failed, cancelled, expired, a wrong PIN, a lock) is a row of the desk's activity log with
/// the actor "<c>owner via Ira</c>"; an account close adds a row for each run and position it closed, skipped or could
/// not close. The PIN is never stored or logged: only its salted hash, in <c>system_settings</c> under
/// <c>ai.actions.pin.&lt;user id&gt;</c>.
/// </para>
/// </remarks>
/// <param name="scopes">Gives each item of an account close its own scope, so its stops run at the same time
/// (<see cref="IraAccountCloser"/>); without it they run one after another.</param>
public sealed class IraActions(
    TradingDbContext db,
    IIraActionHands hands,
    IraPinGuard guard,
    IOptionsMonitor<AiSettings> settings,
    ILogger<IraActions> logger,
    TimeProvider? time = null,
    IServiceScopeFactory? scopes = null)
{
    /// <summary>Where an owner's PIN hash is kept, by console user id.</summary>
    public const string PinKeyPrefix = "ai.actions.pin.";

    /// <summary>One confirm at a time across the API: the row's move from pending to confirmed is decided under it.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public bool Enabled => settings.CurrentValue.Actions.Enabled;

    /// <summary>What a confirm is told while the desk-wide switch is off: a kill switch stops what is waiting too.</summary>
    public const string OffText = "Actions are switched off on this desk (Ai:Actions:Enabled): nothing was done.";

    public int ConfirmSeconds => Math.Max(10, settings.CurrentValue.Actions.ConfirmSeconds);

    public static string PinKey(long userId) => PinKeyPrefix + userId.ToString(CultureInfo.InvariantCulture);

    // ---------- owners ----------

    /// <summary>The console account behind <paramref name="userId"/>, while it is an active admin; else null.</summary>
    public async Task<IraOwner?> OwnerAsync(long? userId, CancellationToken cancellationToken)
    {
        if (userId is not long id) return null;
        var user = await db.AppUsers.AsNoTracking()
            .Where(u => u.Id == id && u.IsActive && u.Role == UserRoles.Admin)
            .Select(u => new { u.Id, u.UserName })
            .FirstOrDefaultAsync(cancellationToken);
        return user is null ? null : new IraOwner(user.Id, user.UserName);
    }

    /// <summary>The console account a Telegram owner is linked to, while it is an active admin; else null.</summary>
    public async Task<IraOwner?> OwnerAsync(string userName, CancellationToken cancellationToken)
    {
        var user = await db.AppUsers.AsNoTracking()
            .Where(u => u.UserName == userName && u.IsActive && u.Role == UserRoles.Admin)
            .Select(u => new { u.Id, u.UserName })
            .FirstOrDefaultAsync(cancellationToken);
        return user is null ? null : new IraOwner(user.Id, user.UserName);
    }

    /// <summary>
    /// Who a proposal is for, or the refusal the model reads: actions switched off, not a chat, not an admin, no PIN,
    /// or locked after wrong PINs.
    /// </summary>
    public async Task<IraOwner> ProposerAsync(AiToolCaller? caller, CancellationToken cancellationToken)
    {
        if (!Enabled) throw new AiToolArgumentException("Actions are switched off on this desk (Ai:Actions:Enabled).");
        if (caller is null || caller.AgentKey != AiCatalog.DeskAssistant || !AiCatalog.ChatSources.Contains(caller.Source, StringComparer.Ordinal))
        {
            throw new AiToolArgumentException("Actions are proposed only by the Desk Assistant, in a chat with the owner.");
        }

        var owner = await OwnerAsync(caller.UserId, cancellationToken)
            ?? throw new AiToolArgumentException("Only an admin's actions can be proposed.");
        if (!await HasPinAsync(owner.UserId, cancellationToken)) throw new AiToolArgumentException(IraActionText.NoPin);
        if (guard.LockedUntil(owner.UserId) is DateTime until) throw new AiToolArgumentException(IraActionText.Locked(until));
        return owner;
    }

    // ---------- the PIN ----------

    public async Task<bool> HasPinAsync(long userId, CancellationToken cancellationToken)
    {
        string key = PinKey(userId);
        return await db.SystemSettings.AsNoTracking().AnyAsync(s => s.Key == key && s.Value != string.Empty, cancellationToken);
    }

    /// <summary>When the owner's actions unlock, while they are locked.</summary>
    public DateTime? LockedUntil(long userId) => guard.LockedUntil(userId);

    /// <summary>
    /// Sets the owner's PIN. On Telegram (<paramref name="requireCurrent"/>) a PIN already set changes only with the
    /// right <paramref name="current"/> one, and a wrong one counts toward the lock; the console, signed in as the
    /// admin, sets or resets it without.
    /// </summary>
    public async Task<(IraPinChange Change, int Left, DateTime? LockedUntilUtc)> SetPinAsync(
        IraOwner owner, string newPin, string? current, bool requireCurrent, string via, CancellationToken cancellationToken)
    {
        if (!IraPin.IsShape(newPin)) return (IraPinChange.BadShape, 0, null);
        string key = PinKey(owner.UserId);
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        bool hasOne = row is not null && row.Value.Length > 0;
        if (hasOne && requireCurrent)
        {
            if (guard.LockedUntil(owner.UserId) is DateTime until) return (IraPinChange.Locked, 0, until);
            if (current is null) return (IraPinChange.NeedCurrent, 0, null);
            if (!IraPin.Verify(current, row!.Value))
            {
                var (left, lockedUntil) = guard.Wrong(owner.UserId);
                if (lockedUntil is DateTime locked) LogLocked(owner, locked);
                await AuditAsync(owner, null, "pin-change", "wrong-pin", 403, false, $"A wrong PIN was given to change the Action PIN ({via}).", cancellationToken);
                return (IraPinChange.WrongCurrent, left, lockedUntil);
            }

            guard.Right(owner.UserId);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (row is null)
        {
            row = new SystemSetting { Key = key, CreatedUtc = now };
            db.SystemSettings.Add(row);
        }

        row.Value = IraPin.Hash(newPin);
        row.UpdatedBy = owner.UserName;
        row.Reason = $"Action PIN, set on {via}";
        row.UpdatedUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        await AuditAsync(owner, null, "pin-set", hasOne ? "changed" : "set", 200, true, $"{(hasOne ? "Changed" : "Set")} the Action PIN on {via}.", cancellationToken);
        logger.LogInformation("Ira actions: {User} {What} the Action PIN on {Via}", owner.UserName, hasOne ? "changed" : "set", via);
        return (hasOne ? IraPinChange.Changed : IraPinChange.Set, 0, null);
    }

    // ---------- proposals ----------

    /// <summary>
    /// Stores <paramref name="proposal"/> as the conversation's pending action; any older one still waiting there is
    /// cancelled (or marked expired), so the PIN always confirms the action just shown.
    /// </summary>
    public async Task<AiAction> ProposeAsync(IraOwner owner, AiToolCaller caller, IraProposal proposal, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        string channel = caller.Source == AiActionChannel.Telegram ? AiActionChannel.Telegram : AiActionChannel.Console;
        string conversation = caller.ConversationId.Length <= 64 ? caller.ConversationId : caller.ConversationId[..64];

        var older = await db.AiActions
            .Where(a => a.ConversationId == conversation && a.UserId == owner.UserId && a.Status == AiActionStatus.Pending)
            .ToListAsync(cancellationToken);
        var action = new AiAction
        {
            CreatedUtc = now,
            ExpiresUtc = now.AddSeconds(ConfirmSeconds),
            UserId = owner.UserId,
            UserName = owner.UserName,
            Channel = channel,
            ConversationId = conversation,
            ChatId = channel == AiActionChannel.Telegram && conversation.StartsWith("tg-", StringComparison.Ordinal)
                     && long.TryParse(conversation[3..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long chat) ? chat : null,
            CallId = caller.CallId,
            Kind = proposal.Kind,
            ParamsJson = proposal.ParamsJson,
            Summary = Cut(proposal.Summary, 1000),
            Status = AiActionStatus.Pending,
        };
        db.AiActions.Add(action);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var old in older)
        {
            if (old.ExpiresUtc <= now)
            {
                Close(old, AiActionStatus.Expired, "Not confirmed in time.");
                Audit(old, "expired", 410, false, $"Expired unconfirmed ({old.Channel}): {old.Summary}");
            }
            else
            {
                Close(old, AiActionStatus.Cancelled, $"Replaced by action #{action.Id}.");
                Audit(old, "cancelled", 200, false, $"Replaced by action #{action.Id} before it was confirmed ({old.Channel}): {old.Summary}");
            }
        }

        if (older.Count > 0) await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Ira actions: #{Id} proposed for {User} on {Channel}: {Summary}", action.Id, owner.UserName, channel, action.Summary);
        return action;
    }

    /// <summary>The pending action of a conversation (a Telegram chat's), newest first; null when none waits.</summary>
    public Task<AiAction?> PendingAsync(string conversationId, long userId, CancellationToken cancellationToken) =>
        db.AiActions.AsNoTracking()
            .Where(a => a.ConversationId == conversationId && a.UserId == userId && a.Status == AiActionStatus.Pending)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The newest action of a conversation, whatever its status; null when there is none.</summary>
    public Task<AiAction?> LatestAsync(string conversationId, long userId, CancellationToken cancellationToken) =>
        db.AiActions.AsNoTracking()
            .Where(a => a.ConversationId == conversationId && a.UserId == userId)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The action a model call proposed, if it proposed one that still waits.</summary>
    public Task<AiAction?> ProposedByAsync(long callId, CancellationToken cancellationToken) =>
        db.AiActions.AsNoTracking()
            .Where(a => a.CallId == callId && a.Status == AiActionStatus.Pending)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(cancellationToken);

    // ---------- confirming ----------

    /// <summary>
    /// The owner's PIN for action <paramref name="actionId"/>, proposed on <paramref name="channel"/>: refused while
    /// locked, after it expired, or with a wrong PIN; with the right one it is carried out once, as the owner.
    /// </summary>
    public async Task<IraConfirmResult> ConfirmAsync(long actionId, IraOwner owner, string? pin, string channel, CancellationToken cancellationToken)
    {
        if (!Enabled) return new IraConfirmResult(IraConfirmKind.Off, OffText, null);

        AiAction action;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var found = await db.AiActions.FirstOrDefaultAsync(a => a.Id == actionId, cancellationToken);
            if (found is null || found.UserId != owner.UserId || found.Channel != channel)
            {
                return new IraConfirmResult(IraConfirmKind.NotFound, "No such action is waiting for you.", null);
            }

            action = found;
            if (action.Status != AiActionStatus.Pending) return new IraConfirmResult(IraConfirmKind.NotPending, IraActionText.NotWaiting(action), action);

            if (guard.LockedUntil(owner.UserId) is DateTime until)
            {
                return new IraConfirmResult(IraConfirmKind.Locked, IraActionText.Locked(until) + " Nothing was done.", action, 0, until);
            }

            var now = _time.GetUtcNow().UtcDateTime;
            if (now > action.ExpiresUtc)
            {
                Close(action, AiActionStatus.Expired, "Not confirmed in time.");
                Audit(action, "expired", 410, false, $"Expired before the PIN ({action.Channel}): {action.Summary}");
                await db.SaveChangesAsync(CancellationToken.None);
                return new IraConfirmResult(IraConfirmKind.Expired, IraActionText.Expired(action), action);
            }

            string key = PinKey(owner.UserId);
            string? stored = await db.SystemSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);
            if (string.IsNullOrEmpty(stored)) return new IraConfirmResult(IraConfirmKind.NoPin, IraActionText.NoPin, action);

            if (!IraPin.Verify(pin, stored))
            {
                var (left, lockedUntil) = guard.Wrong(owner.UserId);
                if (lockedUntil is DateTime locked) LogLocked(owner, locked);
                Audit(action, lockedUntil is null ? "wrong-pin" : "locked", lockedUntil is null ? 403 : 423, false,
                    $"Wrong PIN for action #{action.Id} ({action.Channel}){(lockedUntil is DateTime l ? $"; actions locked until {IraActionText.Ist(l)}" : string.Empty)}: {action.Summary}");
                await db.SaveChangesAsync(CancellationToken.None);
                return new IraConfirmResult(lockedUntil is null ? IraConfirmKind.WrongPin : IraConfirmKind.Locked,
                    IraActionText.WrongPin(left, lockedUntil), action, left, lockedUntil);
            }

            guard.Right(owner.UserId);
            action.Status = AiActionStatus.Confirmed;
            action.ConfirmedUtc = now;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        finally
        {
            Gate.Release();
        }

        return await ExecuteAsync(action, owner);
    }

    /// <summary>Cancels the owner's action <paramref name="actionId"/> while it waits.</summary>
    public async Task<IraConfirmResult> CancelAsync(long actionId, IraOwner owner, string channel, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var action = await db.AiActions.FirstOrDefaultAsync(a => a.Id == actionId, cancellationToken);
            if (action is null || action.UserId != owner.UserId || action.Channel != channel)
            {
                return new IraConfirmResult(IraConfirmKind.NotFound, "No such action is waiting for you.", null);
            }

            if (action.Status != AiActionStatus.Pending) return new IraConfirmResult(IraConfirmKind.NotPending, IraActionText.NotWaiting(action), action);

            var now = _time.GetUtcNow().UtcDateTime;
            if (now > action.ExpiresUtc)
            {
                Close(action, AiActionStatus.Expired, "Not confirmed in time.");
                Audit(action, "expired", 410, false, $"Expired before the PIN ({action.Channel}): {action.Summary}");
                await db.SaveChangesAsync(CancellationToken.None);
                return new IraConfirmResult(IraConfirmKind.Expired, IraActionText.Expired(action), action);
            }

            Close(action, AiActionStatus.Cancelled, $"Cancelled by {owner.UserName}.");
            Audit(action, "cancelled", 200, false, $"Cancelled by the owner ({action.Channel}): {action.Summary}");
            await db.SaveChangesAsync(CancellationToken.None);
            return new IraConfirmResult(IraConfirmKind.Cancelled, IraActionText.Cancelled(action), action);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Carries out a confirmed action as the owner and records how it ended. The cancellation token is not passed on:
    /// once confirmed, an action is finished even if the asker goes away, as a stop the Stop button claimed is.
    /// </summary>
    private async Task<IraConfirmResult> ExecuteAsync(AiAction action, IraOwner owner)
    {
        IraOutcome outcome;
        IraCloseAccount? closed = null;
        IReadOnlyList<IraCloseItem> items = [];
        try
        {
            if (action.Kind == AiActionKind.CloseAccount)
            {
                closed = Read<IraCloseAccount>(action);
                var result = await new IraAccountCloser(db, hands, scopes, logger, _time).CloseAsync(owner, closed);
                (outcome, items) = (result.Outcome, result.Items);
            }
            else
            {
                outcome = action.Kind switch
                {
                    AiActionKind.StopRun => await hands.StopRunAsync(owner, Read<IraStopRun>(action), CancellationToken.None),
                    AiActionKind.StartRun => await hands.StartRunAsync(owner, Read<IraStartRun>(action), CancellationToken.None),
                    AiActionKind.SquareOff => await hands.SquareOffAsync(owner, Read<IraSquareOff>(action), CancellationToken.None),
                    AiActionKind.AgentSwitch => await hands.SwitchAgentAsync(owner, Read<IraAgentSwitch>(action), CancellationToken.None),
                    _ => IraOutcome.Refused($"{action.Kind} is not an action this desk knows"),
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ira actions: #{Id} ({Kind}) failed on the server", action.Id, action.Kind);
            outcome = IraOutcome.Refused($"it failed on the server ({ex.GetType().Name}); see api.log");
        }

        // Whatever a service left half-written is not saved with this record: the services save their own work.
        db.ChangeTracker.Clear();
        var row = await db.AiActions.FirstAsync(a => a.Id == action.Id, CancellationToken.None);
        var now = _time.GetUtcNow().UtcDateTime;
        row.Status = outcome.Done ? AiActionStatus.Executed : AiActionStatus.Failed;
        row.ExecutedUtc = now;
        row.ResultJson = JsonSerializer.Serialize(new { message = outcome.Message, result = outcome.Result }, IraActionResolver.Json);
        row.Error = outcome.Done ? string.Empty : Cut(outcome.Message, 1000);
        foreach (var item in items) AuditItem(row, closed!.Account, item);
        // An account close's summary lists what it closed and each item has its row, so its own row keeps the reply whole;
        // the reply says itself whether it was done, partly done or not done.
        Audit(row, outcome.Done ? "executed" : "failed", outcome.Done ? 200 : 409, outcome.Done, closed is null
            ? $"{(outcome.Done ? "Executed" : "Not done")} via Ira ({row.Channel}): {row.Summary} {outcome.Message}"
            : $"Close everything in {closed.Account}'s account via Ira ({row.Channel}). {outcome.Message}");
        await db.SaveChangesAsync(CancellationToken.None);
        logger.LogInformation("Ira actions: #{Id} ({Kind}) for {User}: {Message}", row.Id, row.Kind, owner.UserName, outcome.Message);
        return new IraConfirmResult(outcome.Done ? IraConfirmKind.Executed : IraConfirmKind.Failed, outcome.Message, row);
    }

    /// <summary>What the owner's line says about an action, from its stored result: "Done: …" or "Not done: …".</summary>
    public static string? ResultMessage(AiAction action)
    {
        if (string.IsNullOrEmpty(action.ResultJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(action.ResultJson);
            return doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static T Read<T>(AiAction action) =>
        JsonSerializer.Deserialize<T>(action.ParamsJson, IraActionResolver.Json)
        ?? throw new InvalidOperationException($"Action #{action.Id} has no parameters.");

    private void Close(AiAction action, string status, string why)
    {
        action.Status = status;
        action.Error = why;
    }

    private void LogLocked(IraOwner owner, DateTime until) =>
        // Information, not a warning: a lock is the desk working as meant, and Sentinel must not open an incident for it.
        logger.LogInformation("Ira actions: {User}'s actions are locked until {Until} after {Count} wrong PINs",
            owner.UserName, IraActionText.Ist(until), IraPinGuard.MaxWrong);

    // ---------- the activity log ----------

    /// <summary>
    /// One activity-log row for an action's outcome, as the middleware writes one for a request: the owner's account,
    /// the actor "<c>owner via Ira</c>", the module the action touches, and a sentence. Never the PIN.
    /// </summary>
    private void Audit(AiAction action, string outcome, int status, bool succeeded, string summary)
    {
        db.ActivityLog.Add(new ActivityLogEntry
        {
            OccurredUtc = _time.GetUtcNow().UtcDateTime,
            UserId = action.UserId,
            UserName = Cut(IraActionText.Actor(action.UserName), 100),
            Role = UserRoles.Admin,
            Module = action.Kind == AiActionKind.AgentSwitch ? "ai" : "strategies",
            Action = $"ira-{action.Kind.Replace('_', '-')}-{outcome}",
            Method = "IRA",
            Path = $"ira/actions/{action.Id}/{outcome}",
            StatusCode = status,
            DurationMs = 0,
            Succeeded = succeeded,
            TargetType = TargetType(action.Kind),
            TargetId = TargetId(action),
            Summary = Cut(summary, 500),
        });
    }

    /// <summary>
    /// One activity-log row for one item of an account close: the run or position, and how it ended
    /// (<c>ira-close-account-run-done</c>, <c>…-position-already-closed</c>, <c>…-run-failed</c>). The whole action's
    /// own row follows them.
    /// </summary>
    private void AuditItem(AiAction action, string account, IraCloseItem item)
    {
        string state = IraAccountCloser.StateWord(item.State);
        db.ActivityLog.Add(new ActivityLogEntry
        {
            OccurredUtc = _time.GetUtcNow().UtcDateTime,
            UserId = action.UserId,
            UserName = Cut(IraActionText.Actor(action.UserName), 100),
            Role = UserRoles.Admin,
            Module = "strategies",
            Action = $"ira-close-account-{item.Type}-{state}",
            Method = "IRA",
            Path = $"ira/actions/{action.Id}/{item.Type}/{item.Id.ToString(CultureInfo.InvariantCulture)}",
            StatusCode = item.State switch { IraCloseState.Done => 200, IraCloseState.AlreadyClosed => 410, _ => 409 },
            DurationMs = 0,
            Succeeded = item.State == IraCloseState.Done,
            TargetType = item.Type,
            TargetId = item.Id.ToString(CultureInfo.InvariantCulture),
            Summary = Cut(IraActionText.CloseItemLine(action, account, item), 500),
        });
    }

    private async Task AuditAsync(IraOwner owner, AiAction? action, string what, string outcome, int status, bool succeeded, string summary, CancellationToken cancellationToken)
    {
        db.ActivityLog.Add(new ActivityLogEntry
        {
            OccurredUtc = _time.GetUtcNow().UtcDateTime,
            UserId = owner.UserId,
            UserName = Cut(IraActionText.Actor(owner.UserName), 100),
            Role = UserRoles.Admin,
            Module = "ai",
            Action = $"ira-{what}-{outcome}",
            Method = "IRA",
            Path = action is null ? $"ira/pin/{outcome}" : $"ira/actions/{action.Id}/{outcome}",
            StatusCode = status,
            Succeeded = succeeded,
            TargetType = "pin",
            Summary = Cut(summary, 500),
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string TargetType(string kind) => kind switch
    {
        AiActionKind.StopRun or AiActionKind.StartRun => "run",
        AiActionKind.SquareOff => "position",
        AiActionKind.CloseAccount => "account",
        _ => "agent",
    };

    /// <summary>What the action acted on: the run, the book, the account, the agent; the run a start made once it ran.</summary>
    private static string? TargetId(AiAction action)
    {
        try
        {
            using var doc = JsonDocument.Parse(action.ParamsJson);
            var root = doc.RootElement;
            string? id = action.Kind switch
            {
                AiActionKind.StopRun when root.TryGetProperty("runId", out var r) => r.ToString(),
                AiActionKind.SquareOff when root.TryGetProperty("bookRunId", out var b) => b.ToString(),
                AiActionKind.CloseAccount when root.TryGetProperty("accountUserId", out var u) => u.ToString(),
                AiActionKind.AgentSwitch when root.TryGetProperty("agent", out var a) => a.GetString(),
                AiActionKind.StartRun => StartedRun(action.ResultJson),
                _ => null,
            };
            return id is null ? null : Cut(id, 60);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StartedRun(string? resultJson)
    {
        if (string.IsNullOrEmpty(resultJson)) return null;
        using var doc = JsonDocument.Parse(resultJson);
        return doc.RootElement.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object
               && r.TryGetProperty("runId", out var id) && id.ValueKind == JsonValueKind.Number ? id.ToString() : null;
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
