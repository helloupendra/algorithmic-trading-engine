using System.Diagnostics;
using System.Net;
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static AlgoTrading.UnitTests.DeskWriteAccessTests;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The Filter lab starts a Python process per request, on the box that runs the
/// live desk: two at once, one per account, and none outlives its request.
/// </summary>
/// <remarks>
/// Until 2 Nov a trader could fire any number of requests and hang up: nothing
/// capped them, and <c>using var process</c> does not stop a process, so each ran
/// on. These start real processes through the real controller; the evaluator is a
/// stand-in that says when it started, and answers at once or sleeps.
/// </remarks>
public sealed class StrategyLabGateTests : IDisposable
{
    private const string Evaluate = "/api/StrategyLab/evaluate";
    private const string Slow = """{"symbol":"SLOW"}""";
    private const string Quick = """{"symbol":"QUICK"}""";
    private const long OtherTrader = 9;
    private const long ThirdTrader = 10;

    private readonly string _engine = Directory.CreateTempSubdirectory("strategy-lab-").FullName;

    public StrategyLabGateTests()
    {
        Directory.CreateDirectory(Path.Combine(_engine, "tools"));
        File.WriteAllText(Path.Combine(_engine, "tools", "evaluate_filters.py"), """
            import json, os, sys, time
            from pathlib import Path
            request = json.load(sys.stdin)
            if request["symbol"] == "QUICK":
                print(json.dumps({"answered": True}))
                sys.exit(0)
            started = Path(__file__).parent / f"started-{os.getpid()}"
            started.write_text("")
            time.sleep(55)
            """);
    }

    public void Dispose()
    {
        foreach (var pid in StartedPids())
        {
            try
            {
                Process.GetProcessById(pid).Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // Stopped, as it should be.
            }
        }

        try
        {
            Directory.Delete(_engine, recursive: true);
        }
        catch (IOException)
        {
            // A process on its way out may still hold the directory on Windows.
        }
    }

    private int[] StartedPids() => Directory.GetFiles(Path.Combine(_engine, "tools"), "started-*")
        .Select(f => int.Parse(Path.GetFileName(f)["started-".Length..], System.Globalization.CultureInfo.InvariantCulture))
        .ToArray();

    private Task<Host> LabHost() => Host.StartAsync<StrategyLabController>(s =>
    {
        s.AddSingleton(EveryModuleGranted.Create());
        s.AddSingleton<StrategyLabGate>();
        s.AddSingleton<PythonEngineLocator>();
        s.Configure<StrategyRunnerOptions>(o =>
        {
            o.EngineDirectory = _engine;
            o.PythonExecutable = OperatingSystem.IsWindows() ? "python" : "python3";
        });
    });

    private async Task<int> NextStartedAsync(IReadOnlyCollection<int> known)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            var fresh = StartedPids().Except(known).ToList();
            if (fresh.Count > 0) return fresh[0];
            await Task.Delay(25);
        }

        throw new TimeoutException("The evaluator did not start.");
    }

    private static async Task AssertStoppedAsync(int pid)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) return;
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Process {pid} is still running after its request ended.");
    }

    /// <summary>The slot is given back just after the process is told to stop, so this may take a moment.</summary>
    private static async Task AssertSlotFreeAsync(Host host, long caller)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        var status = await host.SendAsync("POST", Evaluate, Quick, caller);
        while (status == HttpStatusCode.TooManyRequests && DateTime.UtcNow < until)
        {
            await Task.Delay(25);
            status = await host.SendAsync("POST", Evaluate, Quick, caller);
        }

        Assert.Equal(HttpStatusCode.OK, status);
    }

    private static async Task HangUpAsync(Task<HttpStatusCode> request, CancellationTokenSource client)
    {
        await client.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    // ---------------------------------------------------------------- the gate

    [Fact]
    public void An_account_gets_one_slot_and_the_box_two()
    {
        var gate = new StrategyLabGate();

        using var first = gate.TryEnter(TraderId);
        Assert.NotNull(first);
        Assert.Null(gate.TryEnter(TraderId));

        using var second = gate.TryEnter(OtherTrader);
        Assert.NotNull(second);
        Assert.Null(gate.TryEnter(ThirdTrader));
    }

    [Fact]
    public void A_slot_given_back_is_free_again_once_however_often_it_is_disposed()
    {
        var gate = new StrategyLabGate();

        var first = gate.TryEnter(TraderId)!;
        using var second = gate.TryEnter(OtherTrader);
        first.Dispose();
        first.Dispose();

        // Disposed twice, it would have made room for two.
        using var third = gate.TryEnter(TraderId);
        Assert.NotNull(third);
        Assert.Null(gate.TryEnter(ThirdTrader));
    }

    [Fact]
    public void Many_callers_at_once_never_get_more_than_the_box_holds()
    {
        var gate = new StrategyLabGate();

        var slots = Enumerable.Range(0, 200).AsParallel().Select(i => gate.TryEnter(i)).Where(x => x is not null).ToList();

        Assert.Equal(StrategyLabGate.MaxProcesses, slots.Count);
    }

    // ---------------------------------------------------------------- the endpoint

    [Fact]
    public async Task An_evaluation_answers_and_gives_its_slot_back()
    {
        await using var host = await LabHost();

        using var answer = await host.ExchangeAsync("POST", Evaluate, Quick, TraderId);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Contains("answered", await answer.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, await host.SendAsync("POST", Evaluate, Quick, TraderId));
    }

    [Fact]
    public async Task A_request_past_the_cap_is_refused_and_starts_no_process()
    {
        await using var host = await LabHost();
        var gate = host.Services.GetRequiredService<StrategyLabGate>();

        // This account's slot is taken.
        using (gate.TryEnter(TraderId))
        {
            using var refused = await host.ExchangeAsync("POST", Evaluate, Slow, TraderId);
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            Assert.NotNull(refused.Headers.RetryAfter);
            Assert.Contains("busy", await refused.Content.ReadAsStringAsync());

            // Another account has room.
            Assert.Equal(HttpStatusCode.OK, await host.SendAsync("POST", Evaluate, Quick, OtherTrader));
        }

        // The box is full, with this account holding nothing.
        using (gate.TryEnter(OtherTrader))
        using (gate.TryEnter(ThirdTrader))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, await host.SendAsync("POST", Evaluate, Slow, TraderId));
        }

        Assert.Empty(StartedPids());
        Assert.Equal(HttpStatusCode.OK, await host.SendAsync("POST", Evaluate, Quick, TraderId));
    }

    [Fact]
    public async Task A_client_that_hangs_up_takes_its_process_with_it_and_frees_its_slot()
    {
        await using var host = await LabHost();
        using var client = new CancellationTokenSource();

        var running = host.SendAsync("POST", Evaluate, Slow, TraderId, client.Token);
        int pid = await NextStartedAsync([]);

        // While it runs, the account's slot is taken.
        Assert.Equal(HttpStatusCode.TooManyRequests, await host.SendAsync("POST", Evaluate, Quick, TraderId));

        await HangUpAsync(running, client);
        await AssertStoppedAsync(pid);
        await AssertSlotFreeAsync(host, TraderId);
    }

    [Fact]
    public async Task Hanging_up_on_several_leaves_no_process_behind()
    {
        await using var host = await LabHost();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();

        var one = host.SendAsync("POST", Evaluate, Slow, TraderId, first.Token);
        int onePid = await NextStartedAsync([]);
        var two = host.SendAsync("POST", Evaluate, Slow, OtherTrader, second.Token);
        int twoPid = await NextStartedAsync([onePid]);

        // Two running: a third account is refused, and starts nothing.
        Assert.Equal(HttpStatusCode.TooManyRequests, await host.SendAsync("POST", Evaluate, Slow, ThirdTrader));
        Assert.Equal(2, StartedPids().Length);

        await HangUpAsync(one, first);
        await HangUpAsync(two, second);
        await AssertStoppedAsync(onePid);
        await AssertStoppedAsync(twoPid);
    }

    [Fact]
    public async Task Anonymous_is_refused()
    {
        await using var host = await LabHost();

        Assert.Equal(HttpStatusCode.Unauthorized, await host.SendAsync("POST", Evaluate, Quick, userId: null));
    }
}
