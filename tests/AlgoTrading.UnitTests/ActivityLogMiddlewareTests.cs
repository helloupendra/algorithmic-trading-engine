using AlgoTrading.Api.Services;
using AlgoTrading.Application.Exceptions;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The activity log's row for a request, whatever the request did to its own
/// database context.
/// </summary>
/// <remarks>
/// On 28 Sep a watchlist upsert lost an insert race (23505 on
/// IX_live_watchlist_Symbol). The refused entity stayed tracked in the request's
/// context, the middleware saved its row on that same context, the insert was
/// sent again and failed again, and the request's activity row was lost. The
/// in-memory provider enforces no unique index, so a re-sent insert here
/// succeeds instead: a watchlist row in the database is the proof that the log
/// sent it.
/// </remarks>
public class ActivityLogMiddlewareTests
{
    private const string Path = "/api/LiveData/watchlist";

    [Fact]
    public async Task A_request_whose_save_failed_is_still_recorded_and_its_own_exception_goes_on()
    {
        using var desk = new Desk();
        var race = WatchlistUpsertRaceTests.UniqueViolation();
        var context = desk.Post(Path);

        var thrown = await Assert.ThrowsAsync<DbUpdateException>(() => desk.Middleware(async ctx =>
        {
            // What LiveDataService did on 28 Sep: add the row, and fail to save it.
            var db = ctx.RequestServices.GetRequiredService<TradingDbContext>();
            db.LiveWatchlistItems.Add(new LiveWatchlistItem { Symbol = "NSE:NIFTY2593025000CE" });
            await Task.Yield();
            throw race;
        }).InvokeAsync(context));

        Assert.Same(race, thrown);
        await using var check = desk.Db();
        var row = Assert.Single(await check.ActivityLog.ToListAsync());
        Assert.Equal(Path, row.Path);
        Assert.Equal("POST", row.Method);
        Assert.Equal("data", row.Module);
        // The exception handler answers it with a 500 after this row is written;
        // the row says so, not the 200 the response still carried.
        Assert.Equal(StatusCodes.Status500InternalServerError, row.StatusCode);
        Assert.False(row.Succeeded);
        Assert.Empty(await check.LiveWatchlistItems.ToListAsync());
    }

    [Fact]
    public async Task A_risk_violation_is_recorded_with_the_409_the_caller_gets()
    {
        using var desk = new Desk();

        await Assert.ThrowsAsync<RiskViolationException>(() => desk.Middleware(_ =>
            throw new RiskViolationException("Daily loss limit reached.")).InvokeAsync(desk.Post("/api/Simulator/orders")));

        await using var check = desk.Db();
        var row = Assert.Single(await check.ActivityLog.ToListAsync());
        Assert.Equal(StatusCodes.Status409Conflict, row.StatusCode);
        Assert.False(row.Succeeded);
    }

    [Fact]
    public async Task A_change_the_request_did_not_save_is_not_saved_by_the_log()
    {
        using var desk = new Desk();

        await desk.Middleware(ctx =>
        {
            var db = ctx.RequestServices.GetRequiredService<TradingDbContext>();
            db.LiveWatchlistItems.Add(new LiveWatchlistItem { Symbol = "NSE:NIFTY50-INDEX" });
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        }).InvokeAsync(desk.Post(Path));

        await using var check = desk.Db();
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.Single(await check.ActivityLog.ToListAsync()).StatusCode);
        Assert.Empty(await check.LiveWatchlistItems.ToListAsync());
    }

    [Fact]
    public async Task A_log_that_cannot_be_written_never_replaces_the_request_s_own_outcome()
    {
        using var desk = new Desk(new EverySaveFails());
        var own = new InvalidOperationException("the request's own failure");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            desk.Middleware(_ => throw own).InvokeAsync(desk.Post(Path)));
        Assert.Same(own, thrown);

        // And a request that worked still works.
        var context = desk.Post(Path);
        await desk.Middleware(_ => Task.CompletedTask).InvokeAsync(context);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    /// <summary>
    /// The API's services for these tests: one in-memory database, the
    /// request's own scope, and the middleware as Program.cs builds it.
    /// </summary>
    private sealed class Desk : IDisposable
    {
        private readonly string _name = $"activity-log-{Guid.NewGuid():N}";
        private readonly ServiceProvider _services;
        private readonly List<IServiceScope> _requests = [];

        public Desk(IInterceptor? interceptor = null)
        {
            var services = new ServiceCollection();
            services.AddDbContext<TradingDbContext>(o =>
            {
                o.UseInMemoryDatabase(_name);
                if (interceptor is not null) o.AddInterceptors(interceptor);
            });
            _services = services.BuildServiceProvider();
        }

        public ActivityLogMiddleware Middleware(RequestDelegate endpoint) =>
            new(endpoint, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ActivityLogMiddleware>.Instance);

        public DefaultHttpContext Post(string path)
        {
            var scope = _services.CreateScope();
            _requests.Add(scope);
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = path;
            return context;
        }

        public TradingDbContext Db() =>
            new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(_name).Options);

        public void Dispose()
        {
            foreach (var scope in _requests) scope.Dispose();
            _services.Dispose();
        }
    }

    private sealed class EverySaveFails : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
            => throw new DbUpdateException("the database is not answering");
    }
}
