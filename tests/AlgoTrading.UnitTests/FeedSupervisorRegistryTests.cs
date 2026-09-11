using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Providers;
using AlgoTrading.Infrastructure.Providers;
using AlgoTrading.Infrastructure.Providers.Fyers;
using AlgoTrading.Infrastructure.Providers.Replay;
using AlgoTrading.Infrastructure.Providers.TrueData;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The live feeds: which connectors get one, how each is launched, and which
/// running process each may adopt and kill.
/// </summary>
/// <remarks>
/// Every feed runs the same script, so the only thing that tells one vendor's
/// process from another's is the "--vendor" argument. The rule that matters
/// most here is the one a reader is least likely to notice is broken: a stop
/// aimed at one feed must never be satisfied by another feed's process. Before
/// the feeds shared a script, the TrueData supervisor had its own marker for
/// exactly that reason; these tests keep the guarantee now that they do.
/// <para>
/// Nothing here starts a process or opens a database. Building a supervisor
/// touches neither; only status, start and stop do.
/// </para>
/// </remarks>
public class FeedSupervisorRegistryTests
{
    private sealed class FakeHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "AlgoTrading.Api";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Test";
    }

    private static ProviderDescriptor LiveVendor(string key, string displayName, int rank) => new(
        key, displayName, ProviderKind.Data, ProviderAuthKind.ApiKey,
        new ProviderCapabilities { LiveTicks = true })
    {
        FallbackRank = rank,
    };

    private static (FeedSupervisorRegistry Registry, IngestorSupervisor Ingestor) Build(params ProviderDescriptor[] shipped)
    {
        var seed = new ProviderCatalogSeed();
        foreach (var descriptor in shipped) seed.Add(descriptor);

        var engine = new PythonEngineLocator(Options.Create(new StrategyRunnerOptions()), new FakeHostEnvironment());
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var ingestor = new IngestorSupervisor(engine, scopes, NullLogger<IngestorSupervisor>.Instance);

        return (new FeedSupervisorRegistry(seed, ingestor, engine, scopes, NullLoggerFactory.Instance), ingestor);
    }

    /// <summary>What <c>ps -o command=</c> shows for a feed the supervisor launched.</summary>
    private static string LaunchedCommandLine(PythonDaemonSupervisor supervisor)
    {
        var d = supervisor.Descriptor;
        return $"/srv/app/.venv/bin/python /srv/app/src/AlgoTrading.PythonEngine/{string.Join('/', d.ScriptParts)} {string.Join(' ', d.Args ?? Array.Empty<string>())}";
    }

    // ------------------------------------------------------------ which feeds

    [Fact]
    public void Every_connector_that_declares_live_ticks_gets_a_feed_and_no_other_does()
    {
        var (registry, _) = Build(ReplayProvider.Descriptor, TrueDataProvider.Descriptor, FyersProvider.Descriptor);

        Assert.Equal(new[] { "fyers", "truedata" }, registry.All.Select(f => f.Key));
        Assert.Null(registry.Get(ReplayProvider.Key));
        Assert.Null(registry.Get("nope"));
    }

    [Fact]
    public void Live_vendors_are_listed_in_fallback_order_whatever_order_they_registered_in()
    {
        var (registry, _) = Build(
            LiveVendor("zeta", "Zeta", rank: 5),
            TrueDataProvider.Descriptor,
            LiveVendor("alpha", "Alpha", rank: 5),
            FyersProvider.Descriptor);

        Assert.Equal(new[] { "fyers", "alpha", "zeta", "truedata" }, registry.All.Select(f => f.Key));
    }

    [Fact]
    public void A_key_is_looked_up_without_regard_to_case()
    {
        var (registry, _) = Build(FyersProvider.Descriptor, TrueDataProvider.Descriptor);

        Assert.Same(registry.Get("truedata"), registry.Get("TrueData"));
    }

    // ------------------------------------------------------------- the FYERS feed

    [Fact]
    public void Fyers_is_the_ingestor_instance_so_both_routes_drive_one_process()
    {
        var (registry, ingestor) = Build(FyersProvider.Descriptor, TrueDataProvider.Descriptor);

        Assert.Same(ingestor, registry.Get("fyers"));
        Assert.Same(ingestor, registry.All.Single(f => f.Key == "fyers").Supervisor);
    }

    [Fact]
    public void The_ingestor_launches_run_feed_and_keeps_its_name_and_pid_key()
    {
        var (_, ingestor) = Build(FyersProvider.Descriptor);
        var d = ingestor.Descriptor;

        Assert.Equal("ingestor", d.Name);
        Assert.Equal("ingestor.pid", d.PidSettingKey);
        Assert.Equal(new[] { "market_data", "live", "run_feed.py" }, d.ScriptParts);
        Assert.Equal(new[] { "--vendor", "fyers" }, d.Args);
        Assert.Equal("--vendor fyers", d.ProcessMarker);
        Assert.Equal(new[] { "fyers_streamer" }, d.LegacyMarkers);
    }

    [Fact]
    public void A_fyers_streamer_started_before_the_deploy_is_still_adopted()
    {
        var (_, ingestor) = Build(FyersProvider.Descriptor);

        Assert.True(ProcessProbe.NamesAnyMarker(
            "/srv/app/.venv/bin/python /srv/app/src/AlgoTrading.PythonEngine/market_data/live/fyers_streamer.py",
            ingestor.Markers));
    }

    // -------------------------------------------------------- every other feed

    [Fact]
    public void TrueData_is_described_like_any_vendor_and_still_answers_to_its_old_script()
    {
        var (registry, _) = Build(FyersProvider.Descriptor, TrueDataProvider.Descriptor);
        var feed = registry.Get("truedata");

        Assert.IsType<FeedSupervisor>(feed);
        var d = feed!.Descriptor;
        Assert.Equal("TrueData feed", d.Name);
        Assert.Equal("feed.truedata.pid", d.PidSettingKey);
        Assert.Equal(new[] { "market_data", "live", "run_feed.py" }, d.ScriptParts);
        Assert.Equal(new[] { "--vendor", "truedata" }, d.Args);
        Assert.Equal("--vendor truedata", d.ProcessMarker);
        Assert.Equal(new[] { "truedata_streamer" }, d.LegacyMarkers);
    }

    [Fact]
    public void A_vendor_added_later_gets_a_feed_with_no_code_of_its_own()
    {
        var (registry, _) = Build(FyersProvider.Descriptor, LiveVendor("acme", "Acme", rank: 20));
        var d = registry.Get("acme")!.Descriptor;

        Assert.Equal("Acme feed", d.Name);
        Assert.Equal("feed.acme.pid", d.PidSettingKey);
        Assert.Equal(new[] { "--vendor", "acme" }, d.Args);
        Assert.Equal("--vendor acme", d.ProcessMarker);
        Assert.Null(d.LegacyMarkers);
    }

    // ------------------------------------------------ a stop cannot cross feeds

    [Fact]
    public void No_feed_recognises_another_feeds_process_or_shares_its_pid_key()
    {
        // "fyers-paper" is here for its name: "--vendor fyers" is a prefix of
        // "--vendor fyers-paper", which is the collision a bare substring check
        // would miss.
        var (registry, _) = Build(
            FyersProvider.Descriptor,
            TrueDataProvider.Descriptor,
            LiveVendor("fyers-paper", "FYERS paper", rank: 30),
            LiveVendor("truedata2", "TrueData 2", rank: 40));

        Assert.Equal(registry.All.Count, registry.All.Select(f => f.Supervisor.Descriptor.PidSettingKey).Distinct().Count());

        foreach (var owner in registry.All)
        {
            foreach (var other in registry.All)
            {
                bool recognised = ProcessProbe.NamesAnyMarker(LaunchedCommandLine(other.Supervisor), owner.Supervisor.Markers);
                Assert.True(recognised == (owner.Key == other.Key),
                    $"the {owner.Key} feed {(recognised ? "recognises" : "does not recognise")} a process launched for {other.Key}");
            }
        }
    }

    // ------------------------------------------------------------- the marker rule

    [Theory]
    [InlineData("python run_feed.py --vendor fyers", "--vendor fyers", true)]
    [InlineData("PYTHON RUN_FEED.PY --VENDOR FYERS", "--vendor fyers", true)]
    [InlineData("python run_feed.py --vendor fyers --verbose", "--vendor fyers", true)]
    [InlineData("python run_feed.py --vendor truedata", "--vendor fyers", false)]
    [InlineData("python run_feed.py --vendor fyers-paper", "--vendor fyers", false)]
    [InlineData("python run_feed.py --vendor fyers_paper", "--vendor fyers", false)]
    [InlineData("python run_feed.py --vendor fyers2", "--vendor fyers", false)]
    // A longer name first must not hide the exact one later on the line.
    [InlineData("python run_feed.py --vendor fyers-paper --vendor fyers", "--vendor fyers", true)]
    // The markers that predate the feeds all end at ".py" or a space and must keep matching.
    [InlineData("/v/bin/python /e/market_data/live/fyers_streamer.py", "fyers_streamer", true)]
    [InlineData("/v/bin/python /e/market_data/live/option_chain_poller.py", "option_chain_poller", true)]
    [InlineData("/v/bin/python /e/strategies/execution_runner.py --run-id 12", "execution_runner", true)]
    [InlineData("/v/bin/python -m strategies.execution_runner --run-id 12", "execution_runner", true)]
    [InlineData("/v/bin/python /e/backtesting/backtest_runner.py --run-id 3", "backtest_runner", true)]
    [InlineData("/v/bin/python /e/../../scripts/telegram_notifier.py --no-forward", "telegram_notifier", true)]
    [InlineData("/usr/bin/vim notes.txt", "fyers_streamer", false)]
    public void A_marker_counts_only_when_it_is_not_the_start_of_a_longer_name(string commandLine, string marker, bool expected)
    {
        Assert.Equal(expected, ProcessProbe.NamesAnyMarker(commandLine, new[] { marker }));
    }

    [Fact]
    public void An_empty_marker_recognises_nothing()
    {
        // string.Contains("") is true for every string; a descriptor with a
        // blank marker must not adopt, and so kill, whatever holds a stale pid.
        Assert.False(ProcessProbe.NamesAnyMarker("python anything.py", new[] { "" }));
    }
}
