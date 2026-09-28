namespace AlgoTrading.Application.Interfaces;

/// <summary>The kinds of <see cref="DeskEvent"/>, as the console receives them.</summary>
public static class DeskEventKinds
{
    /// <summary>A signal was booked: the order rows of a run or a manual book changed.</summary>
    public const string Order = "order";

    /// <summary>A leg filled (one per filled leg).</summary>
    public const string Fill = "fill";

    /// <summary>A run started, is stopping, stopped, was adopted, or had its risk rules changed.</summary>
    public const string Run = "run";

    /// <summary>A risk rule or the kill switch acted: a leg, a group or a whole run was closed.</summary>
    public const string Risk = "risk";

    /// <summary>A position closed, was settled at expiry, or had its own levels set.</summary>
    public const string Position = "position";

    /// <summary>A position's carry-forward tick changed, or a leg moved to the manual book at the close.</summary>
    public const string Carry = "carry";
}

/// <summary>
/// "Something on the desk changed": enough for a page to know what to fetch
/// again, never the data itself.
/// </summary>
/// <param name="Kind">One of <see cref="DeskEventKinds"/>.</param>
/// <param name="RunId">The run (or manual book) it happened in, when there is one.</param>
/// <param name="UserId">The run's owner, whose connections are told; admins are always told.</param>
/// <param name="Symbol">The contract, when the event is about one.</param>
/// <param name="AtUtc">When it happened on the server.</param>
/// <param name="Detail">
/// A short line a person can read, or null. Never a secret or a token. Cut at
/// <see cref="MaxDetailLength"/>: a reason can be free text typed by a person.
/// </param>
public sealed record DeskEvent(
    string Kind,
    long? RunId,
    long? UserId,
    string? Symbol,
    DateTime AtUtc,
    string? Detail)
{
    /// <summary>The longest <see cref="Detail"/> sent, the ellipsis included.</summary>
    public const int MaxDetailLength = 200;

    public string? Detail { get; init; } =
        Detail is { Length: > MaxDetailLength } ? Detail[..(MaxDetailLength - 1)] + "…" : Detail;
}

/// <summary>
/// Tells the console, the moment a write has committed, that an order, a
/// fill, a run, a risk rule, a position or a carry changed.
/// </summary>
/// <remarks>
/// The pages used to poll every few seconds to find out; with this they
/// fetch again when told. An event is a hint, not a record: one that is lost
/// costs a page a refresh on its slower timer, never data.
/// <para>
/// Publish only after the write is committed — a page told too early fetches
/// the old state and then has nothing left to tell it. Implementations must
/// neither throw nor wait on a browser: every caller is booking a fill,
/// stopping a run or closing a leg, and none of that may wait on, or fail
/// because of, a screen.
/// </para>
/// </remarks>
public interface IDeskEventPublisher
{
    void Publish(DeskEvent deskEvent);
}

public static class DeskEventPublisherExtensions
{
    /// <summary>
    /// Publishes, and makes sure a publisher's failure never reaches the caller.
    /// </summary>
    /// <remarks>
    /// Implementations already swallow and log their own failures (the SignalR
    /// one at Debug); this is the backstop for one that does not, because the
    /// write it reports on has committed by now and an exception here would
    /// turn a booked fill into a failed request.
    /// </remarks>
    public static void TryPublish(this IDeskEventPublisher? publisher, DeskEvent deskEvent)
    {
        if (publisher is null) return;

        try
        {
            publisher.Publish(deskEvent);
        }
        catch (Exception)
        {
            // See the remarks: the write has committed; the event is a hint.
        }
    }
}
