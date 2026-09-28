using System.Security.Claims;
using AlgoTrading.Api.Hubs;
using AlgoTrading.Application.Interfaces;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;

namespace AlgoTrading.UnitTests;

/// <summary>
/// A hub context that records what was sent where, and can be told to fail or
/// hold a send to one connection.
/// </summary>
/// <remarks>
/// A real hub needs a server and a browser; what the dispatcher and the desk
/// event publisher decide — who gets a message, and what is in it — is plain
/// logic, and this is the one object they talk to.
/// </remarks>
internal sealed class RecordingHubContext : IHubContext<LiveFeedHub>
{
    private readonly object _lock = new();
    private readonly List<Sent> _sent = new();

    public RecordingHubContext()
    {
        Clients = new HubClients(this);
        Groups = new GroupManager(this);
    }

    /// <param name="Target">"client:{id}", "group:{name}" or "group:{name} except {ids}".</param>
    public sealed record Sent(string Target, string Method, object?[] Args);

    public IHubClients Clients { get; }

    public IGroupManager Groups { get; }

    /// <summary>
    /// What a send to <c>client:{id}</c> does before it is recorded: return a
    /// task to hold it, throw to fail it. Null records it and completes.
    /// </summary>
    public Func<string, Task>? OnSend { get; set; }

    /// <summary>Group memberships made through <see cref="Groups"/>, as "connection → group".</summary>
    public List<(string ConnectionId, string Group)> Joined { get; } = new();

    public IReadOnlyList<Sent> All
    {
        get { lock (_lock) return _sent.ToList(); }
    }

    public void Clear()
    {
        lock (_lock) _sent.Clear();
    }

    /// <summary>The ticks each connection was sent, one list per message, in order.</summary>
    public List<List<LiveTickPush>> TicksTo(string connectionId)
        => All.Where(x => x.Target == $"client:{connectionId}" && x.Method == LiveTickDispatcher.ReceiveTicks)
            .Select(x => ((IEnumerable<LiveTickPush>)x.Args[0]!).ToList())
            .ToList();

    private async Task RecordAsync(string target, string method, object?[] args)
    {
        if (OnSend is { } onSend)
        {
            await onSend(target);
        }
        lock (_lock) _sent.Add(new Sent(target, method, args));
    }

    private sealed class Proxy(RecordingHubContext owner, string target) : ISingleClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            => owner.RecordAsync(target, method, args);

        public Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken cancellationToken)
            => throw new NotSupportedException("Nothing here asks a browser for an answer.");
    }

    private sealed class HubClients(RecordingHubContext owner) : IHubClients
    {
        public IClientProxy All => new Proxy(owner, "all");

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds)
            => new Proxy(owner, $"all except {string.Join(",", excludedConnectionIds)}");

        public ISingleClientProxy Client(string connectionId) => new Proxy(owner, $"client:{connectionId}");

        IClientProxy IHubClients<IClientProxy>.Client(string connectionId) => Client(connectionId);

        public IClientProxy Clients(IReadOnlyList<string> connectionIds)
            => new Proxy(owner, $"clients:{string.Join(",", connectionIds)}");

        public IClientProxy Group(string groupName) => new Proxy(owner, $"group:{groupName}");

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds)
            => new Proxy(owner, $"group:{groupName} except {string.Join(",", excludedConnectionIds)}");

        public IClientProxy Groups(IReadOnlyList<string> groupNames)
            => new Proxy(owner, $"groups:{string.Join(",", groupNames)}");

        public IClientProxy User(string userId) => new Proxy(owner, $"user:{userId}");

        public IClientProxy Users(IReadOnlyList<string> userIds) => new Proxy(owner, $"users:{string.Join(",", userIds)}");
    }

    private sealed class GroupManager(RecordingHubContext owner) : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            lock (owner._lock) owner.Joined.Add((connectionId, groupName));
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            lock (owner._lock) owner.Joined.Remove((connectionId, groupName));
            return Task.CompletedTask;
        }
    }
}

/// <summary>Keeps every desk event it is given, in order.</summary>
internal sealed class RecordingDeskEvents : IDeskEventPublisher
{
    private readonly object _lock = new();
    private readonly List<DeskEvent> _events = new();

    public IReadOnlyList<DeskEvent> All
    {
        get { lock (_lock) return _events.ToList(); }
    }

    public IReadOnlyList<DeskEvent> Of(string kind) => All.Where(x => x.Kind == kind).ToList();

    public void Publish(DeskEvent deskEvent)
    {
        lock (_lock) _events.Add(deskEvent);
    }

    public void Clear()
    {
        lock (_lock) _events.Clear();
    }
}

/// <summary>The caller a hub method sees: a connection id and who signed in on it.</summary>
internal sealed class TestCallerContext(string connectionId, ClaimsPrincipal? user) : HubCallerContext
{
    public override string ConnectionId => connectionId;
    public override string? UserIdentifier => null;
    public override ClaimsPrincipal? User => user;
    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
    public override IFeatureCollection Features { get; } = new FeatureCollection();
    public override CancellationToken ConnectionAborted => CancellationToken.None;

    /// <summary>Whether the hub aborted this connection.</summary>
    public bool Aborted { get; private set; }

    public override void Abort() => Aborted = true;
}
