using System.Text.RegularExpressions;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// What the owner may change about the AI (tier chains, each agent's switch
/// and own chain), and the catalog it changes: defaults when nothing is
/// stored, the tier chain followed until an agent has its own, and a stored
/// value nobody can read counting as the default rather than breaking calls.
/// </summary>
public class AiSettingsTests
{
    private static readonly IReadOnlySet<string> Known = new HashSet<string>(AiCatalog.DefaultModels) { "meta/llama-4-maverick" };

    [Fact]
    public async Task With_nothing_stored_the_tiers_are_the_defaults_the_assistant_is_on_and_the_scheduled_agents_off()
    {
        var ai = Build();

        var state = await ai.Store.LoadAsync();

        Assert.All(state.Tiers, t => Assert.Equal(t.Def.DefaultChain, t.Chain));
        Assert.All(state.Tiers, t => Assert.False(t.Overridden));
        Assert.Equal(14, state.Agents.Count);
        Assert.Equal("on", state.Agent(AiCatalog.DeskAssistant)!.Status);
        // The scheduled agents call the provider by themselves: off until the owner turns them on.
        string[] scheduled = [AiCatalog.TradeReviewer, AiCatalog.NewsAnalyst, AiCatalog.IncidentExplainer];
        Assert.All(scheduled, key => Assert.Equal("off", state.Agent(key)!.Status));
        Assert.All(state.Agents.Where(a => a.Def.Key != AiCatalog.DeskAssistant && !scheduled.Contains(a.Def.Key)),
            a => Assert.Equal("planned", a.Status));
        Assert.Equal(new[] { Judge1, Judge2, Judge3 }, state.Agent(AiCatalog.DeskAssistant)!.Chain);
    }

    [Fact]
    public async Task An_agent_follows_its_tier_s_chain_until_it_has_its_own()
    {
        var ai = Build();
        await ai.Store.SetTierChainAsync("judge", [Judge2, Judge1], "upendra", "Kimi first this week");

        var followed = (await ai.Store.LoadAsync()).Agent(AiCatalog.DeskAssistant)!;
        Assert.Equal(new[] { Judge2, Judge1 }, followed.Chain);
        Assert.False(followed.ChainOverridden);

        await ai.Store.SetAgentChainAsync(AiCatalog.DeskAssistant, [Judge3], "upendra", null);
        var own = (await ai.Store.LoadAsync()).Agent(AiCatalog.DeskAssistant)!;
        Assert.Equal(new[] { Judge3 }, own.Chain);
        Assert.True(own.ChainOverridden);

        await ai.Store.SetAgentChainAsync(AiCatalog.DeskAssistant, null, "upendra", null);
        await ai.Store.SetTierChainAsync("judge", [], "upendra", null);
        var back = await ai.Store.LoadAsync();
        Assert.Equal(new[] { Judge1, Judge2, Judge3 }, back.Agent(AiCatalog.DeskAssistant)!.Chain);
        Assert.False(back.Tier("judge").Overridden);
    }

    [Fact]
    public async Task Switching_an_agent_off_is_kept_with_who_and_why()
    {
        var ai = Build();

        await ai.Store.SetAgentEnabledAsync(AiCatalog.DeskAssistant, false, "upendra", "  free tier is slow today ");
        var agent = (await ai.Store.LoadAsync()).Agent(AiCatalog.DeskAssistant)!;

        Assert.Equal("off", agent.Status);
        Assert.Equal("upendra", agent.UpdatedBy);
        Assert.Equal("free tier is slow today", agent.Reason);
        Assert.NotNull(agent.UpdatedUtc);
    }

    [Fact]
    public async Task A_planned_agent_stays_off_whatever_a_row_says()
    {
        var ai = Build();
        ai.Db.SystemSettings.Add(new SystemSetting { Key = AiSettingsStore.AgentEnabledKey("technical-analyst"), Value = "true" });
        await ai.Db.SaveChangesAsync();

        Assert.Equal("planned", (await ai.Store.LoadAsync()).Agent("technical-analyst")!.Status);
    }

    [Fact]
    public async Task A_scheduled_agent_switched_on_stays_on()
    {
        var ai = Build();

        await ai.Store.SetAgentEnabledAsync(AiCatalog.TradeReviewer, true, "upendra", "two weeks of reviews");

        Assert.Equal("on", (await ai.Store.LoadAsync()).Agent(AiCatalog.TradeReviewer)!.Status);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("[\"\"]")]
    [InlineData("{\"a\":1}")]
    public async Task A_stored_chain_nobody_can_read_counts_as_the_default(string value)
    {
        var ai = Build();
        ai.Db.SystemSettings.Add(new SystemSetting { Key = AiSettingsStore.TierChainKey("judge"), Value = value });
        await ai.Db.SaveChangesAsync();

        var tier = (await ai.Store.LoadAsync()).Tier("judge");

        Assert.Equal(tier.Def.DefaultChain, tier.Chain);
        Assert.False(tier.Overridden);
    }

    [Theory]
    [InlineData(new string[0], "at least one model")]
    [InlineData(new[] { "a/1", "a/2", "a/3", "a/4", "a/5", "a/6" }, "at most 5")]
    [InlineData(new[] { "moonshotai/kimi-k3", "moonshotai/kimi-k3" }, "twice")]
    [InlineData(new[] { "nope" }, "not a model id")]
    [InlineData(new[] { "vendor/unknown-model" }, "not in the provider's model list")]
    [InlineData(new[] { "nvidia/nemotron-3-embed-1b" }, "vectors, not answers")]
    public void A_chain_the_desk_cannot_walk_is_refused_in_words(string[] chain, string expected)
    {
        Assert.Contains(expected, AiSettingsStore.ChainProblem(chain, Known));
    }

    [Fact]
    public void A_chain_of_known_distinct_chat_models_is_fine()
    {
        Assert.Null(AiSettingsStore.ChainProblem(["meta/llama-4-maverick", Judge1], Known));
    }

    [Fact]
    public void The_catalog_numbers_fourteen_agents_once_each_and_builds_the_phase_three_four()
    {
        Assert.Equal(Enumerable.Range(1, 14), AiCatalog.Agents.Select(a => a.Number));
        Assert.Equal(AiCatalog.Agents.Count, AiCatalog.Agents.Select(a => a.Key).Distinct().Count());
        Assert.Equal(
            new[] { AiCatalog.DeskAssistant, AiCatalog.TradeReviewer, AiCatalog.NewsAnalyst, AiCatalog.IncidentExplainer },
            AiCatalog.Agents.Where(a => a.Built).Select(a => a.Key));
        Assert.All(AiCatalog.Agents.Where(a => a.Built), a => Assert.False(string.IsNullOrWhiteSpace(a.SystemPrompt)));
        Assert.All(AiCatalog.Agents, a => Assert.NotNull(AiCatalog.Tier(a.Tier)));
        Assert.All(AiCatalog.Agents, a => Assert.Contains("order", a.Limits));
    }

    /// <summary>
    /// core/llm.py keeps its own copy of the chains for scripts that call the
    /// provider directly. The two must say the same thing.
    /// </summary>
    [Fact]
    public void The_default_chains_match_core_llm_py()
    {
        string llm = File.ReadAllText(Path.Combine(RepoRoot(), "src", "AlgoTrading.PythonEngine", "core", "llm.py"));
        var block = Regex.Match(llm, @"DEFAULT_CHAINS[^=]*=\s*\{(?<body>.*?)\n\}", RegexOptions.Singleline);
        Assert.True(block.Success, "DEFAULT_CHAINS not found in core/llm.py");

        var python = Regex.Matches(block.Groups["body"].Value, "\"(?<tier>[a-z]+)\":\\s*\\((?<models>[^)]*)\\)")
            .ToDictionary(
                m => m.Groups["tier"].Value,
                m => Regex.Matches(m.Groups["models"].Value, "\"([^\"]+)\"").Select(x => x.Groups[1].Value).ToArray());

        var chat = AiCatalog.Tiers.Where(t => t.Chat).ToList();
        Assert.Equal(chat.Select(t => t.Key).OrderBy(k => k), python.Keys.OrderBy(k => k));
        foreach (var tier in chat) Assert.Equal(tier.DefaultChain, python[tier.Key]);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlgoTrading.slnx")) && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
