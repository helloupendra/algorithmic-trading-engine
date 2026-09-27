using System.Diagnostics;
using AlgoTrading.Api.Configuration;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Contracts.Desk;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AlgoTrading.UnitTests;

/// <summary>
/// GET /api/Desk/plan: config/morning-plan.txt read the way the morning job
/// deploys it and Sentinel checks it, and each run it asks for set against
/// what is live.
/// </summary>
public class DeskPlanTests : IDisposable
{
    /// <summary>The plan as it stood on 27 Sep, with the header trimmed.</summary>
    private const string Fixture = """
        # The morning plan: what scripts/market-open.sh deploys at 08:45 IST.
        #   Strategy  UNDERLYING[,UNDERLYING...]  lots  [legTargetPoints | -]  [@account[,account]]

        accounts: admin coderforchange

        GhostTangentCrossings  BANKNIFTY,NIFTY,SENSEX  2
        ChainFlowBuy           NIFTY                   3  15
            # an indented comment is still a comment
        Fulcrum                BANKNIFTY,NIFTY,SENSEX  2  -  @admin
        CrudeMomentum          CRUDEOIL                2  -
        """;

    private readonly List<Process> _processes = new();
    private readonly string _root = Directory.CreateTempSubdirectory("desk-plan-").FullName;

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
            process.Dispose();
        }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ======================================================== the grammar

    [Fact]
    public void The_plan_reads_its_accounts_lines_lots_and_leg_targets()
    {
        var plan = MorningPlan.Parse(Fixture);

        Assert.Equal(new[] { "admin", "coderforchange" }, plan.Accounts);
        Assert.Equal(new[] { "GhostTangentCrossings", "ChainFlowBuy", "Fulcrum", "CrudeMomentum" }, plan.Lines.Select(l => l.Strategy));
        Assert.Empty(plan.Warnings);

        var ghost = plan.Lines[0];
        Assert.Equal(new[] { "BANKNIFTY", "NIFTY", "SENSEX" }, ghost.Underlyings);
        Assert.Equal((2, MorningPlan.LegTargetDefault, (decimal?)null), (ghost.Lots, ghost.LegTarget, ghost.LegTargetPoints));
        Assert.Empty(ghost.OnlyAccounts);

        var chainFlow = plan.Lines[1];
        Assert.Equal((3, MorningPlan.LegTargetPoints, (decimal?)15m), (chainFlow.Lots, chainFlow.LegTarget, chainFlow.LegTargetPoints));

        var fulcrum = plan.Lines[2];
        Assert.Equal(MorningPlan.LegTargetNone, fulcrum.LegTarget);
        Assert.Equal(new[] { "admin" }, fulcrum.OnlyAccounts);
        Assert.Equal(9, fulcrum.Number);
    }

    [Fact]
    public void An_at_list_limits_a_line_to_the_accounts_it_names()
    {
        var runs = MorningPlan.Parse(Fixture).ExpectedRuns();

        // 3 + 1 + 3 + 1 for admin; Fulcrum is admin's alone, so 3 + 1 + 1 for coderforchange.
        Assert.Equal(8, runs.Count(r => r.Account == "admin"));
        Assert.Equal(5, runs.Count(r => r.Account == "coderforchange"));
        Assert.DoesNotContain(runs, r => r.Account == "coderforchange" && r.Strategy == "Fulcrum");
        Assert.Contains(new MorningPlan.ExpectedRun("admin", "Fulcrum", "SENSEX", 2), runs);
        Assert.Contains(new MorningPlan.ExpectedRun("coderforchange", "ChainFlowBuy", "NIFTY", 3), runs);
    }

    [Fact]
    public void Account_names_compare_in_any_case_as_the_morning_job_compares_them()
    {
        var plan = MorningPlan.Parse("""
            accounts: Admin coderforchange
            Fulcrum  NIFTY  2  -  @ADMIN,somebody
            """);

        Assert.Equal(new[] { "Admin" }, plan.ExpectedRuns().Select(r => r.Account));
        Assert.Contains(plan.Warnings, w => w.Contains("@somebody"));
    }

    [Fact]
    public void Lots_left_off_are_the_jobs_default_and_a_repeat_is_counted_once()
    {
        var plan = MorningPlan.Parse("""
            accounts: admin
            Ghost  NIFTY
            Ghost  NIFTY,NIFTY  4
            """);

        Assert.Equal(MorningPlan.DefaultLots, plan.Lines[0].Lots);
        Assert.Equal(new[] { new MorningPlan.ExpectedRun("admin", "Ghost", "NIFTY", MorningPlan.DefaultLots) }, plan.ExpectedRuns());
    }

    [Fact]
    public void What_the_morning_job_would_read_differently_or_refuse_is_named()
    {
        var plan = MorningPlan.Parse("""
            Ghost  NIFTY  2   # a note
            Lonely
            Fulcrum  NIFTY  two
            Crude  CRUDEOIL  2  lots
            accounts: admin
            accounts: coderforchange
            """);

        Assert.Contains(plan.Warnings, w => w.StartsWith("Line 1:") && w.Contains("comment after the line"));
        Assert.Contains(plan.Warnings, w => w.StartsWith("Line 2:") && w.Contains("no underlying"));
        Assert.Contains(plan.Warnings, w => w.StartsWith("Line 3:") && w.Contains("'two'"));
        Assert.Contains(plan.Warnings, w => w.StartsWith("Line 4:") && w.Contains("'lots'"));
        Assert.Contains(plan.Warnings, w => w.StartsWith("Line 6:") && w.Contains("second 'accounts:'"));
        // Sentinel's reading stands: the note is cut off, lots fall back to 2.
        Assert.Equal(new[] { "admin" }, plan.Accounts);
        Assert.Equal(new[] { "Ghost", "Fulcrum", "Crude" }, plan.Lines.Select(l => l.Strategy));
        Assert.Equal(MorningPlan.DefaultLots, plan.Lines[1].Lots);
    }

    [Fact]
    public void A_plan_without_accounts_says_the_job_will_start_nothing()
    {
        var plan = MorningPlan.Parse("Ghost  NIFTY  2");

        Assert.Empty(plan.ExpectedRuns());
        Assert.Contains(plan.Warnings, w => w.Contains("No 'accounts:' line"));
    }

    // ======================================================== the desk

    [Fact]
    public async Task Each_planned_run_says_whether_it_is_live_and_which_run_it_is()
    {
        using var desk = Desk(WritePlan(Path.Combine(_root, "plan.txt"), Fixture), out var db, out var registry);
        long ghostNifty = AddRun(db, userId: 1, "GhostTangentCrossings", "NIFTY");
        long restarted = AddRun(db, userId: 1, "GhostTangentCrossings", "BANKNIFTY");
        long restartedAgain = AddRun(db, userId: 1, "GhostTangentCrossings", "BANKNIFTY");
        AddRun(db, userId: 2, "GhostTangentCrossings", "NIFTY");                    // its runner is gone
        AddRun(db, userId: 2, "ChainFlowBuy", "NIFTY", status: "Stopped");
        Register(registry, ghostNifty, 1, "GhostTangentCrossings", "NIFTY");
        Register(registry, restarted, 1, "GhostTangentCrossings", "BANKNIFTY");
        Register(registry, restartedAgain, 1, "GhostTangentCrossings", "BANKNIFTY");

        var plan = Ok(await Controller(desk).GetPlan(CancellationToken.None));

        Assert.Equal(13, plan.Planned);
        Assert.Equal(2, plan.Live);
        Assert.Equal(Path.Combine(_root, "plan.txt"), plan.File);

        DeskPlanRun Row(string account, string strategy, string underlying) =>
            plan.Runs.Single(r => r.Account == account && r.Strategy == strategy && r.Underlying == underlying);

        Assert.Equal((true, (long?)ghostNifty), (Row("admin", "GhostTangentCrossings", "NIFTY").IsLive, Row("admin", "GhostTangentCrossings", "NIFTY").RunId));
        Assert.Equal(restartedAgain, Row("admin", "GhostTangentCrossings", "BANKNIFTY").RunId);
        // Running with no runner behind it is a row, not a run; stopped is stopped.
        Assert.False(Row("coderforchange", "GhostTangentCrossings", "NIFTY").IsLive);
        Assert.Null(Row("coderforchange", "GhostTangentCrossings", "NIFTY").RunId);
        Assert.False(Row("coderforchange", "ChainFlowBuy", "NIFTY").IsLive);
        Assert.Equal(2, Row("coderforchange", "ChainFlowBuy", "NIFTY").UserId);
    }

    [Fact]
    public async Task A_planned_account_that_does_not_exist_has_no_user_id_and_nothing_live()
    {
        using var desk = Desk(WritePlan(Path.Combine(_root, "plan.txt"), """
            accounts: admin nobody
            Ghost  NIFTY  2
            """), out _, out _);

        var plan = Ok(await Controller(desk).GetPlan(CancellationToken.None));

        var nobody = plan.Runs.Single(r => r.Account == "nobody");
        Assert.Null(nobody.UserId);
        Assert.False(nobody.IsLive);
        Assert.Equal(1, plan.Runs.Single(r => r.Account == "admin").UserId);
    }

    [Fact]
    public void Without_configuration_the_plan_is_found_from_the_content_root_upwards()
    {
        // src/AlgoTrading.Api under the repository, as under `dotnet run`.
        var contentRoot = Directory.CreateDirectory(Path.Combine(_root, "src", "AlgoTrading.Api")).FullName;
        var file = WritePlan(Path.Combine(_root, "config", "morning-plan.txt"), Fixture);

        using var desk = Desk(planFile: null, out _, out _, contentRoot);
        var builder = desk.ServiceProvider.GetRequiredService<DeskPlanBuilder>();

        Assert.Equal(file, builder.Locate().File);
    }

    [Fact]
    public void A_relative_plan_file_is_taken_from_the_content_root()
    {
        var contentRoot = Directory.CreateDirectory(Path.Combine(_root, "api")).FullName;
        var file = WritePlan(Path.Combine(_root, "elsewhere", "today.txt"), Fixture);

        using var desk = Desk(Path.Combine("..", "elsewhere", "today.txt"), out _, out _, contentRoot);

        Assert.Equal(file, desk.ServiceProvider.GetRequiredService<DeskPlanBuilder>().Locate().File);
    }

    [Fact]
    public async Task No_plan_file_is_a_404_that_says_where_it_looked()
    {
        var missing = Path.Combine(_root, "no-such-plan.txt");
        using var desk = Desk(missing, out _, out _);

        var result = await Controller(desk).GetPlan(CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Contains(missing, System.Text.Json.JsonSerializer.Serialize(notFound.Value));
    }

    [Fact]
    public void The_plan_is_for_admins_only()
    {
        var attribute = Assert.Single(typeof(DeskController).GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true).Cast<AuthorizeAttribute>());
        Assert.Equal(AuthorizationPolicies.AdminOnly, attribute.Policy);
    }

    // ======================================================== helpers

    private static string WritePlan(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private IServiceScope Desk(string? planFile, out TradingDbContext db, out StrategyProcessRegistry registry, string? contentRoot = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        string name = $"desk-plan-{Guid.NewGuid():N}";
        services.AddDbContext<TradingDbContext>(o => o.UseInMemoryDatabase(name));
        services.AddSingleton<StrategyProcessRegistry>();
        services.AddSingleton<IWebHostEnvironment>(new Environment(contentRoot ?? _root));
        services.AddSingleton(Options.Create(new DeskOptions { PlanFile = planFile }));
        services.AddScoped<DeskPlanBuilder>();

        var scope = services.BuildServiceProvider().CreateScope();
        db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        db.AppUsers.AddRange(
            new AppUser { Id = 1, UserName = "admin", Role = UserRoles.Admin, IsActive = true },
            new AppUser { Id = 2, UserName = "coderforchange", Role = UserRoles.Trader, IsActive = true },
            new AppUser { Id = 3, UserName = "gone", Role = UserRoles.Trader, IsActive = false });
        db.SaveChanges();
        registry = scope.ServiceProvider.GetRequiredService<StrategyProcessRegistry>();
        return scope;
    }

    private static DeskController Controller(IServiceScope desk) => new(desk.ServiceProvider.GetRequiredService<DeskPlanBuilder>());

    private static DeskPlanResponse Ok(ActionResult<DeskPlanResponse> result)
        => Assert.IsType<DeskPlanResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static long AddRun(TradingDbContext db, long userId, string strategy, string underlying, string status = "Running")
    {
        var run = new SimulationRun
        {
            UserId = userId, Mode = "LivePaper", Status = status, Symbol = $"NSE:{underlying}-INDEX", StrategyName = strategy,
            ParametersJson = $"{{\"underlying\":\"{underlying}\"}}", LastError = string.Empty, Resolution = "1m",
            ReplaySpeed = string.Empty, StartedUtc = DateTime.UtcNow, CreatedUtc = DateTime.UtcNow
        };
        db.SimulationRuns.Add(run);
        db.SaveChanges();
        return run.Id;
    }

    /// <summary>A registry entry around a real process that stays alive, as the registry requires.</summary>
    private void Register(StrategyProcessRegistry registry, long runId, long userId, string strategy, string underlying)
    {
        var info = TestSleeper.StartInfo();
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.UseShellExecute = false;
        var process = Process.Start(info)!;
        _processes.Add(process);

        Assert.True(registry.TryAdd(new RunningStrategy(
            StrategyCatalogService.StableId(strategy), strategy, process, "admin", userId, DateTime.UtcNow, runId,
            underlying, $"NSE:{underlying}-INDEX", 2, new RiskRulesDto())));
    }

    private sealed class Environment(string contentRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "AlgoTrading.Api";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Test";
    }
}
