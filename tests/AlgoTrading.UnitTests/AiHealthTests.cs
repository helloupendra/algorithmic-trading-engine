using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Which models the desk asks first, from what its own calls have seen: a
/// model queueing on the free tier waits at the back of its chains for a
/// while, and comes back when it answers again.
/// </summary>
public class AiHealthTests
{
    private static readonly DateTime T0 = new(2026, 9, 30, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_failure_that_cost_a_long_wait_cools_a_model_at_once_and_a_quick_one_needs_a_second()
    {
        var health = new AiModelHealth();

        health.Record(Judge3, false, 90, slow: true, "timeout: no answer within 90 s", T0);
        health.Record(Judge2, false, 0.6, slow: false, "provider error: Service temporarily overloaded", T0);

        Assert.Equal("cooling", health.State(Judge3, T0).State);
        Assert.Equal(T0.AddMinutes(10), health.State(Judge3, T0).CoolingUntilUtc);
        Assert.Equal("failed", health.State(Judge2, T0).State);

        health.Record(Judge2, false, 0.5, slow: false, "http 429", T0.AddSeconds(5));
        Assert.Equal("cooling", health.State(Judge2, T0.AddSeconds(5)).State);
    }

    [Fact]
    public void A_model_that_keeps_failing_cools_longer_each_time_up_to_an_hour()
    {
        var health = new AiModelHealth();
        var at = T0;
        var spans = new List<double>();

        for (int i = 0; i < 5; i++)
        {
            health.Record(Judge3, false, 90, slow: true, "timeout", at);
            var until = health.State(Judge3, at).CoolingUntilUtc!.Value;
            spans.Add((until - at).TotalMinutes);
            at = until;
        }

        Assert.Equal(new double[] { 10, 20, 40, 60, 60 }, spans);
    }

    [Fact]
    public void One_answer_makes_a_model_healthy_again()
    {
        var health = new AiModelHealth();
        health.Record(Judge3, false, 90, slow: true, "timeout", T0);

        health.Record(Judge3, true, 7.8, slow: false, "ok", T0.AddMinutes(12));

        var state = health.State(Judge3, T0.AddMinutes(12));
        Assert.Equal("healthy", state.State);
        Assert.Equal(7.8, state.LastOkSeconds);
        Assert.Null(state.CoolingUntilUtc);
    }

    [Fact]
    public void A_cooling_model_moves_to_the_back_of_its_chain_and_its_probe_falls_due_when_the_cooling_ends()
    {
        var health = new AiModelHealth();
        health.Record(Judge1, false, 95, slow: true, "timeout", T0);

        Assert.Equal(new[] { Judge2, Judge3, Judge1 }, health.Order([Judge1, Judge2, Judge3], T0.AddMinutes(1)));
        Assert.Empty(health.DueForProbe(T0.AddMinutes(9)));
        Assert.Equal(new[] { Judge1 }, health.DueForProbe(T0.AddMinutes(10)));
        Assert.Equal(new[] { Judge1, Judge2, Judge3 }, health.Order([Judge1, Judge2, Judge3], T0.AddMinutes(10)));
    }

    [Fact]
    public async Task The_gateway_asks_a_healthy_model_before_a_cooling_one()
    {
        var ai = Build();
        ai.Health.Record(Judge1, false, 95, slow: true, "timeout: no answer within 90 s", DateTime.UtcNow);
        ai.Provider.On(Judge2, Answer("Super, first."));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal(Judge2, result.Model);
        Assert.Equal(new[] { Judge2 }, ai.Provider.Requests.Select(r => r.Model));
    }

    [Fact]
    public async Task A_cooling_model_is_still_asked_when_every_healthy_one_fails()
    {
        var ai = Build();
        ai.Health.Record(Judge1, false, 95, slow: true, "timeout", DateTime.UtcNow);
        ai.Provider.On(Judge2, Script.Status(500));
        ai.Provider.On(Judge3, Script.Status(500));
        ai.Provider.On(Judge1, Answer("The cooling one answered."));

        var result = await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        Assert.Equal(new[] { Judge2, Judge3, Judge1 }, ai.Provider.Requests.Select(r => r.Model));
        Assert.Equal(Judge1, result.Model);
    }

    [Fact]
    public async Task The_gateway_teaches_the_health_table_what_each_attempt_did()
    {
        var ai = Build(Settings(s => s.SlowFailureSeconds = 0.2 ));
        ai.Provider.On(Judge1, Script.Silent(TimeSpan.FromSeconds(0.3)));
        ai.Provider.On(Judge2, Answer("ok"));

        await ai.Gateway.AskAsync(Question(), new RecordingSink(), CancellationToken.None);

        var now = DateTime.UtcNow;
        Assert.Equal("cooling", ai.Health.State(Judge1, now).State);
        Assert.Equal("healthy", ai.Health.State(Judge2, now).State);
    }

    [Fact]
    public async Task The_probe_asks_a_model_whose_cooling_ended_and_an_answer_heals_it()
    {
        var ai = Build();
        ai.Health.Record(Judge3, false, 95, slow: true, "timeout", DateTime.UtcNow.AddMinutes(-11));
        ai.Provider.On(Judge3, Answer("OK"));

        await Probe(ai).ProbeAsync(CancellationToken.None);

        Assert.Equal("healthy", ai.Health.State(Judge3, DateTime.UtcNow).State);
        var call = Assert.Single(ai.Db.AiCalls);
        Assert.Equal(("health", AiCatalog.ModelTest), (call.Source, call.AgentKey));
    }

    [Fact]
    public async Task After_a_restart_the_last_half_hour_of_calls_says_which_models_were_queueing()
    {
        var ai = Build();
        ai.Db.AiCalls.Add(new AiCall
        {
            AgentKey = AiCatalog.DeskAssistant, Outcome = AiCallOutcome.Ok, CreatedUtc = DateTime.UtcNow.AddMinutes(-5),
            AttemptsJson = $$"""[{"model":"{{Judge3}}","outcome":"timeout: no answer within 90 s","seconds":90.02},{"model":"{{Judge2}}","outcome":"ok","seconds":1.1}]""",
        });
        await ai.Db.SaveChangesAsync();

        await Probe(ai).SeedAsync(CancellationToken.None);

        Assert.Equal("cooling", ai.Health.State(Judge3, DateTime.UtcNow).State);
        Assert.Equal("healthy", ai.Health.State(Judge2, DateTime.UtcNow).State);
    }

    private static AiHealthProbe Probe(Services ai)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => ai.Gateway);
        services.AddScoped(_ => ai.Db);
        return new AiHealthProbe(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), ai.Health, ai.Options,
            NullLogger<AiHealthProbe>.Instance);
    }
}
