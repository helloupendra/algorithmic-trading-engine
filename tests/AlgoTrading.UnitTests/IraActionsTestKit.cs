using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.AiActions;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.ValueObjects;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AlgoTrading.UnitTests;

/// <summary>
/// What the tests of Ira's actions share: the owner and his desk, hands that record what they were asked to do, a
/// clock the tests move, and a Telegram bot that records every method. Nothing here reaches a broker, AWS or Telegram.
/// </summary>
internal static class IraActionsTestKit
{
    public const long Admin = 1;
    public const long OtherAdmin = 2;
    public const long Trader = 7;
    public const string Pin = "4321";

    /// <summary>10:30 IST on Monday 12 Oct 2026.</summary>
    public static readonly DateTime Monday1030 = new(2026, 10, 12, 5, 0, 0, DateTimeKind.Utc);

    /// <summary>PBKDF2 at a test's pace: a stored hash keeps its own count, so production's is untouched.</summary>
    public static void FastPins() => IraPin.Iterations = 1_000;

    /// <summary>The owner (upendra, admin), a second admin (ravi) and a trader (coderforchange).</summary>
    public static void SeedUsers(TradingDbContext db)
    {
        db.AppUsers.AddRange(
            new AppUser { Id = Admin, UserName = "upendra", Role = UserRoles.Admin, IsActive = true },
            new AppUser { Id = OtherAdmin, UserName = "ravi", Role = UserRoles.Admin, IsActive = true },
            new AppUser { Id = Trader, UserName = "coderforchange", Role = UserRoles.Trader, IsActive = true });
        db.SaveChanges();
    }

    /// <summary>
    /// Live runs: 512 Ghost on NIFTY (upendra), 530 Ghost on NIFTY (coderforchange), 540 Fulcrum on BANKNIFTY (upendra),
    /// 560 Ghost on SENSEX (stopped); upendra's manual book 550 with two open positions (77, 78) and a closed one (79);
    /// a leg 90 of run 512.
    /// </summary>
    public static void SeedDesk(TradingDbContext db, DateTime nowUtc)
    {
        SimulationRun Run(long id, long user, string strategy, string underlying, string status) => new()
        {
            Id = id, UserId = user, Mode = StrategyRunControl.LivePaperMode, Status = status, StrategyName = strategy,
            Symbol = Infrastructure.Services.UnderlyingCatalog.SpotSymbolFor(underlying), ParametersJson = $$"""{"underlying":"{{underlying}}"}""",
            CreatedUtc = nowUtc.AddHours(-1), StartedUtc = nowUtc.AddHours(-1),
        };

        var book = ManualBook.NewBook(Admin, nowUtc.AddDays(-3));
        book.Id = 550;
        db.SimulationRuns.AddRange(
            Run(512, Admin, "GhostTangentCrossings", "NIFTY", "Running"),
            Run(530, Trader, "GhostTangentCrossings", "NIFTY", "Running"),
            Run(540, Admin, "Fulcrum", "BANKNIFTY", "Running"),
            Run(560, Admin, "GhostTangentCrossings", "SENSEX", "Stopped"),
            book);
        db.PaperPositions.AddRange(
            Position(77, 550, "NSE:NIFTY26OCT25100CE", "Buy", 150, 120.50m, "Open"),
            Position(78, 550, "NSE:NIFTY26OCT25000PE", "Sell", 75, 88m, "Open"),
            Position(79, 550, "NSE:BANKNIFTY26OCT56000CE", "Buy", 30, 300m, "Closed"),
            Position(90, 512, "NSE:NIFTY26OCT25200CE", "Sell", 75, 60m, "Open"));
        db.SaveChanges();

        PaperPosition Position(long id, long run, string symbol, string direction, int qty, decimal avg, string status) => new()
        {
            Id = id, SimulationRunId = run, StrategyName = "Manual", GroupId = $"G{id}", Symbol = symbol, Direction = direction,
            Quantity = qty, AveragePrice = avg, Status = status, OpenedUtc = nowUtc.AddHours(-2),
        };
    }

    /// <summary>Registers Ira's actions in a test's container, as Program.cs does, around these hands and this guard.</summary>
    public static void AddActions(IServiceCollection services, IIraActionHands hands, IraPinGuard guard, IOptionsMonitor<AiSettings> options, TimeProvider time)
    {
        services.AddSingleton(guard);
        services.AddScoped(_ => hands);
        services.AddScoped(sp => new IraActions(sp.GetRequiredService<TradingDbContext>(), hands, guard, options, NullLogger<IraActions>.Instance, time,
            sp.GetRequiredService<IServiceScopeFactory>()));
    }

    /// <summary>Scopes whose hands are <paramref name="hands"/>: what lets an account close run its items at the same time.</summary>
    public static IServiceScopeFactory Scopes(IIraActionHands hands)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => hands);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    public static StrategyProcessRegistry Registry() => new(
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<StrategyProcessRegistry>.Instance);

    /// <summary>A clock a test moves by hand.</summary>
    public sealed class MovableTime(DateTime utc) : TimeProvider
    {
        public DateTime Now { get; set; } = utc;

        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Now += by;
    }

    /// <summary>
    /// Hands that record each action they were asked to carry out and answer as told: done by default, a stop with one
    /// leg squared off and a square-off of every position asked. They count how many calls are in hand at once.
    /// </summary>
    public sealed class FakeHands : IIraActionHands
    {
        private readonly object _gate = new();
        private int _inFlight;

        public List<(string Kind, IraOwner Owner, object Target)> Calls { get; } = [];

        public Func<string, IraOutcome>? Answer { get; set; }

        /// <summary>An answer for one target (a run that fails, a run gone): null falls back to <see cref="Answer"/>, then the default.</summary>
        public Func<string, object, IraOutcome?>? Respond { get; set; }

        /// <summary>Held open by a test that confirms twice at once, or watches how many stops run together.</summary>
        public Task Hold { get; set; } = Task.CompletedTask;

        /// <summary>What <see cref="DayNetAsync"/> says.</summary>
        public decimal? DayNet { get; set; }

        /// <summary>Calls in hand now, and the most there ever were at once.</summary>
        public int InFlight { get { lock (_gate) return _inFlight; } }

        public int MostAtOnce { get; private set; }

        public Task<IraOutcome> StopRunAsync(IraOwner owner, IraStopRun target, CancellationToken cancellationToken) => Run(AiActionKind.StopRun, owner, target);

        public Task<IraOutcome> StartRunAsync(IraOwner owner, IraStartRun target, CancellationToken cancellationToken) => Run(AiActionKind.StartRun, owner, target);

        public Task<IraOutcome> SquareOffAsync(IraOwner owner, IraSquareOff target, CancellationToken cancellationToken) => Run(AiActionKind.SquareOff, owner, target);

        public Task<IraOutcome> SwitchAgentAsync(IraOwner owner, IraAgentSwitch target, CancellationToken cancellationToken) => Run(AiActionKind.AgentSwitch, owner, target);

        public Task<decimal?> DayNetAsync(string account, CancellationToken cancellationToken) => Task.FromResult(DayNet);

        private async Task<IraOutcome> Run(string kind, IraOwner owner, object target)
        {
            lock (_gate)
            {
                Calls.Add((kind, owner, target));
                _inFlight++;
                MostAtOnce = Math.Max(MostAtOnce, _inFlight);
            }

            try
            {
                await Hold;
                return Respond?.Invoke(kind, target) ?? Answer?.Invoke(kind) ?? new IraOutcome(true, $"Done: {kind} for {owner.UserName}.", target switch
                {
                    IraStopRun s => new IraRunStopped(s.RunId, 1, null),
                    IraSquareOff q => new IraSquaredOff(q.BookRunId, q.PositionIds.Count, q.PositionIds, 0m),
                    _ => new { kind },
                });
            }
            finally
            {
                lock (_gate) _inFlight--;
            }
        }
    }

    /// <summary>The account rows as the module grant reads them: everyone has every module unless a test says otherwise.</summary>
    public sealed class Grants(bool allowed = true) : IUserAdminService
    {
        public Task<bool> IsModuleAllowedAsync(long userId, string moduleKey, CancellationToken cancellationToken = default) => Task.FromResult(allowed);

        public Task<IReadOnlyList<AlgoTrading.Contracts.Users.UserAdminResponse>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AlgoTrading.Contracts.Users.UserAdminResponse?> GetAsync(long userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AlgoTrading.Contracts.Users.UserAdminResponse> UpdateAsync(long userId, AlgoTrading.Contracts.Users.UpdateUserRequest request, long actingUserId, string actingUserName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AlgoTrading.Contracts.Users.UserAdminResponse> SetGrantsAsync(long userId, IReadOnlyList<string> moduleKeys, string actingUserName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ResetPasswordAsync(long userId, string newPassword, string actingUserName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ChangeOwnPasswordAsync(long userId, string currentPassword, string newPassword, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> RevokeSessionsAsync(long userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>A day the exchange does not open: every live start is refused, as on a weekend.</summary>
    public sealed class NoSession : IMarketSessionService
    {
        public MarketSessionInfo GetSessionInfo(DateTime utcNow, string exchange, string segment) => new()
        {
            Exchange = exchange, Segment = segment, UtcNow = utcNow, IsTradingDay = false, SessionOpenUtc = utcNow, SessionCloseUtc = utcNow,
        };

        public bool IsMarketOpen(DateTime utcNow, string exchange, string segment) => false;

        public DateTime GetNextMarketOpenUtc(DateTime utcNow, string exchange, string segment) => utcNow.AddDays(1);
    }

    /// <summary>Keeps every log line, to show what is logged and at what level.</summary>
    public sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>api.telegram.org: records every method called with its body, and gives each sent message an id.</summary>
    public sealed class Bot : HttpMessageHandler
    {
        private long _nextMessageId = 100;

        public List<Dictionary<string, string>> Sent { get; } = [];

        public List<string> Texts => Sent.Where(s => s["method"] == "sendMessage").Select(s => s["text"]).ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string method = path[(path.LastIndexOf('/') + 1)..];
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            var entry = body.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty);
            entry["method"] = method;
            long id = Interlocked.Increment(ref _nextMessageId);
            lock (Sent) Sent.Add(entry);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
