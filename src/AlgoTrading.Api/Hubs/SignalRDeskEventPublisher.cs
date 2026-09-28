using AlgoTrading.Application.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace AlgoTrading.Api.Hubs;

/// <summary>The SignalR groups desk events are sent to.</summary>
public static class DeskEventGroups
{
    /// <summary>Every connection of an admin.</summary>
    public const string Admins = "role:admin";

    /// <summary>Every connection of one user.</summary>
    public static string User(long userId) => $"user:{userId}";
}

/// <summary>
/// Sends each <see cref="DeskEvent"/> over the live feed hub as
/// <c>DeskEvent</c>, to the run owner's connections and to every admin's.
/// </summary>
/// <remarks>
/// <para>
/// An admin's connection is in both <c>role:admin</c> and its own
/// <c>user:{id}</c>, and the morning plan runs thirteen strategies in the
/// admin's own account: sent to both groups, the admin's console would have
/// been told of every one of those fills twice. So the owner's group is sent
/// the event without the admins' connections, which have it already.
/// </para>
/// <para>
/// <see cref="Publish"/> returns at once and never throws: the send runs on
/// its own, and a failure is logged at Debug and forgotten. The caller has
/// just booked a fill or stopped a run, and a browser is no reason to wait
/// or to fail.
/// </para>
/// <para>
/// The admins' send and the owner's start together. A group send completes
/// when every member's write has, and until 28 Sep the owner's waited for the
/// admins': one admin phone on a bad network, with the whole feed open, held
/// every trader's fills and stops until its socket drained or timed out
/// (about 30 s), while their pages polled at the slow connected pace.
/// </para>
/// </remarks>
public sealed class SignalRDeskEventPublisher : IDeskEventPublisher
{
    /// <summary>The client method desk events arrive on.</summary>
    public const string ClientMethod = "DeskEvent";

    private readonly IHubContext<LiveFeedHub> _hub;
    private readonly LiveFeedSubscriptions _subscriptions;
    private readonly ILogger<SignalRDeskEventPublisher> _logger;

    public SignalRDeskEventPublisher(
        IHubContext<LiveFeedHub> hub,
        LiveFeedSubscriptions subscriptions,
        ILogger<SignalRDeskEventPublisher> logger)
    {
        _hub = hub;
        _subscriptions = subscriptions;
        _logger = logger;
    }

    public void Publish(DeskEvent deskEvent)
    {
        try
        {
            _ = SendAsync(deskEvent);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Desk event {Kind} for run {RunId} was not sent.", deskEvent.Kind, deskEvent.RunId);
        }
    }

    /// <summary>The event as the console reads it; the task never faults.</summary>
    internal async Task SendAsync(DeskEvent deskEvent)
    {
        try
        {
            var payload = new
            {
                kind = deskEvent.Kind,
                runId = deskEvent.RunId,
                userId = deskEvent.UserId,
                symbol = deskEvent.Symbol,
                atUtc = AsUtc(deskEvent.AtUtc),
                detail = deskEvent.Detail,
            };

            var toAdmins = SendToAsync(_hub.Clients.Group(DeskEventGroups.Admins), payload, deskEvent);
            var toOwner = deskEvent.UserId is { } owner
                ? SendToAsync(_hub.Clients.GroupExcept(DeskEventGroups.User(owner), _subscriptions.AdminConnectionIds()), payload, deskEvent)
                : Task.CompletedTask;

            await Task.WhenAll(toAdmins, toOwner);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Desk event {Kind} for run {RunId} could not be pushed.", deskEvent.Kind, deskEvent.RunId);
        }
    }

    /// <summary>One group's send; the task never faults, so one group's failure is not the other's.</summary>
    private async Task SendToAsync(IClientProxy clients, object payload, DeskEvent deskEvent)
    {
        try
        {
            await clients.SendAsync(ClientMethod, payload);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Desk event {Kind} for run {RunId} could not be pushed.", deskEvent.Kind, deskEvent.RunId);
        }
    }

    // Serialised with its Z only when the kind says UTC; a value read back
    // from the database comes out Unspecified.
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
