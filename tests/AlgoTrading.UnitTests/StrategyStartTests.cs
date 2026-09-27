using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlgoTrading.Api.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The one way a live runner starts: POST /api/Strategy/{id}/start.
/// </summary>
/// <remarks>
/// Three things went wrong here before 28 Sep. The run row was inserted before
/// the duplicate and desk-wide checks, so every refusal left a Failed run in
/// the history. Two overlapping starts could both pass the checks and both
/// launch. And a second endpoint, POST /api/Strategy/{id}/deploy, put a runner
/// behind an existing row: it checked for a duplicate in the row's owner's
/// account but launched in the caller's, never checked that the caller owned
/// the row or that it was still open, and set it back to Running — so anyone
/// with the Strategies module could put a runner behind another trader's run.
/// Nothing called it any more; it is gone.
/// </remarks>
public class StrategyStartTests
{
    private const long OneGigabyte = 1024L * 1024 * 1024;

    [Fact]
    public async Task ConcurrentStarts_OneWins_NoFailedRow()
    {
        using var desk = new RunnerDesk();

        // A double click, or the morning job racing the console.
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => desk.Start("NIFTY"))));

        Assert.Single(results, r => r is OkObjectResult);
        Assert.All(results.Where(r => r is not OkObjectResult), r => Assert.IsType<ConflictObjectResult>(r));

        var run = Assert.Single(desk.Runs());
        Assert.Equal("Running", run.Status);
        Assert.Equal(1, desk.Registry.Count);
        Assert.Equal(run.Id, desk.Registry.List().Single().RunId);
    }

    [Theory]
    [InlineData("desk")]     // StrategyRunner:MaxConcurrentProcesses, every account's runners together
    [InlineData("account")]  // the Risk limits' per-account cap
    public async Task CapReached_Returns429_NoRow(string cap)
    {
        using var desk = new RunnerDesk();
        if (cap == "desk") desk.Options.MaxConcurrentProcesses = 1;
        else desk.MaxRunsPerAccount = 1;

        Assert.IsType<OkObjectResult>(await desk.Start("NIFTY"));

        var refused = Assert.IsType<ObjectResult>(await desk.Start("BANKNIFTY"));
        Assert.Equal(429, refused.StatusCode);
        Assert.Contains("limit", Message(refused), StringComparison.OrdinalIgnoreCase);

        // The desk-wide cap used to be checked after the insert: a Failed row per refusal.
        Assert.Single(desk.Runs());
        Assert.Equal(1, desk.Registry.Count);
    }

    [Fact]
    public async Task OpenDbRow_Returns409()
    {
        using var desk = new RunnerDesk();

        // Still Running in the database with no runner the API knows of: the
        // startup reconcile could not verify its pid, so it neither adopted
        // nor closed it. A second runner would trade the same book twice.
        long stuck = desk.SeedRun(RunnerDesk.AdminId, "NIFTY", "Running");

        var refused = Assert.IsType<ConflictObjectResult>(await desk.Start("NIFTY"));
        Assert.Contains($"run #{stuck}", Message(refused));
        Assert.Equal(0, desk.Registry.Count);

        // Stopping counts as open too: the previous API was still ending it.
        desk.SeedRun(RunnerDesk.AdminId, "BANKNIFTY", "Stopping");
        Assert.IsType<ConflictObjectResult>(await desk.Start("BANKNIFTY"));

        Assert.Equal(2, desk.Runs().Count);
    }

    [Fact]
    public async Task An_open_row_blocks_only_its_own_account_and_underlying()
    {
        using var desk = new RunnerDesk();
        desk.SeedRun(RunnerDesk.TraderId, "NIFTY", "Running");          // another account
        desk.SeedRun(RunnerDesk.AdminId, "BANKNIFTY", "Stopped");        // closed
        desk.SeedRun(RunnerDesk.AdminId, "NIFTY", "Running", "Fulcrum"); // another strategy

        Assert.IsType<OkObjectResult>(await desk.Start("NIFTY"));
        Assert.IsType<OkObjectResult>(await desk.Start("BANKNIFTY"));
    }

    [Fact]
    public async Task LowMemory_Returns429()
    {
        using var desk = new RunnerDesk { AvailableMemoryBytes = 900L * 1024 * 1024 };

        var refused = Assert.IsType<ObjectResult>(await desk.Start("NIFTY"));
        Assert.Equal(429, refused.StatusCode);
        Assert.Contains("900 MB", Message(refused));
        Assert.Empty(desk.Runs());

        desk.AvailableMemoryBytes = 2 * OneGigabyte;
        Assert.IsType<OkObjectResult>(await desk.Start("NIFTY"));
    }

    [Fact]
    public async Task Memory_that_cannot_be_read_does_not_stop_a_start()
    {
        // Not Linux, or /proc unreadable: unknown is not "none left".
        using var desk = new RunnerDesk { AvailableMemoryBytes = null };
        Assert.IsType<OkObjectResult>(await desk.Start("NIFTY"));
    }

    [Fact]
    public async Task Post_deploy_answers_404()
    {
        // A real host routing the API's own controllers: the question is what
        // the router answers, which reflection over attributes would only guess.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(StrategyController).Assembly);

        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();

        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        var deploy = await http.PostAsync("/api/Strategy/650824872/deploy", JsonContent.Create(new { runId = 46 }));
        Assert.Equal(HttpStatusCode.NotFound, deploy.StatusCode);

        // The same host does route the start endpoint (it fails there for want
        // of services, not of a route), so the 404 above is the route being
        // gone rather than a host that routes nothing.
        var start = await http.PostAsync("/api/Strategy/650824872/start", JsonContent.Create(new { underlying = "NIFTY" }));
        Assert.NotEqual(HttpStatusCode.NotFound, start.StatusCode);

        await app.StopAsync();
    }

    private static string Message(ObjectResult result)
        => JsonSerializer.SerializeToElement(result.Value).GetProperty("message").GetString() ?? string.Empty;
}
