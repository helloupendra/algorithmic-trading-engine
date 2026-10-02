using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Talking with every agent: the owner's console or Telegram question to the AI Trader, the Trade Reviewer, the News
/// Analyst or the Incident Explainer gets that agent's chat persona and the tools that read its own work; its
/// scheduled work keeps its task prompt; and the AI Trader's chat reads only lessons its next look would read.
/// </summary>
public sealed class AiAgentChatTests
{
    private static readonly string[] Chatty = [AiCatalog.AiTrader, AiCatalog.TradeReviewer, AiCatalog.NewsAnalyst, AiCatalog.IncidentExplainer];

    // ---------- the persona, by where the question came from ----------

    [Theory]
    [InlineData("console")]
    [InlineData("telegram")]
    public async Task A_chat_with_an_agent_gets_its_persona_and_its_chat_tools(string source)
    {
        foreach (string key in Chatty)
        {
            var agent = AiCatalog.Agent(key)!;
            var ai = Build(tools: ChatToolsOf(agent));
            await ai.Store.SetAgentEnabledAsync(key, true, "upendra", null);
            ai.Provider.On(FirstOf(agent), Answer("I read my own records first."));

            var result = await ai.Gateway.AskAsync(Question("What did you do today?", key) with { Source = source }, new RecordingSink(), CancellationToken.None);

            Assert.Equal(AiCallOutcome.Ok, result.Outcome);
            var body = JsonNode.Parse(ai.Provider.Requests.Single().Body)!;
            string system = body["messages"]![0]!["content"]!.GetValue<string>();
            Assert.StartsWith(agent.ChatPrompt, system);
            Assert.DoesNotContain("Reply with one JSON object", system);
            Assert.Contains("Now: ", system);
            var offered = body["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
            Assert.Equal(agent.ChatTools, offered);
            Assert.StartsWith(agent.ChatPrompt, (await ai.Db.AiCalls.SingleAsync()).SystemPrompt);
        }
    }

    [Theory]
    [InlineData("schedule")]
    [InlineData("check")]
    [InlineData("api")]
    public async Task Its_scheduled_work_keeps_its_task_prompt_and_tools(string source)
    {
        var agent = AiCatalog.Agent(AiCatalog.TradeReviewer)!;
        var ai = Build(tools: ChatToolsOf(agent));
        await ai.Store.SetAgentEnabledAsync(agent.Key, true, "upendra", null);
        ai.Provider.On(Judge1, Answer("""{"verdict":"followed","journal":"ok"}"""));

        await ai.Gateway.AskAsync(Question("Review run 412.", agent.Key) with { Source = source }, new RecordingSink(), CancellationToken.None);

        var body = JsonNode.Parse(ai.Provider.Requests.Single().Body)!;
        Assert.StartsWith(agent.SystemPrompt, body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal(agent.Tools, body["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList());
    }

    [Fact]
    public async Task The_desk_assistant_is_unchanged_in_a_chat()
    {
        var desk = AiCatalog.Agent(AiCatalog.DeskAssistant)!;
        Assert.Equal(string.Empty, desk.ChatPrompt);
        var ai = Build(tools: ChatToolsOf(desk));
        ai.Provider.On(Judge1, Answer("ok"));

        await ai.Gateway.AskAsync(Question("How did the runs do?"), new RecordingSink(), CancellationToken.None);

        var body = JsonNode.Parse(ai.Provider.Requests.Single().Body)!;
        Assert.StartsWith(desk.SystemPrompt, body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal(AiToolNames.Desk, body["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList());
    }

    [Fact]
    public async Task A_caller_with_its_own_prompt_gets_no_persona_even_from_the_console()
    {
        var ai = Build(tools: ChatToolsOf(AiCatalog.Agent(AiCatalog.AiTrader)!));
        await ai.Store.SetAgentEnabledAsync(AiCatalog.AiTrader, true, "upendra", null);
        ai.Provider.On(Judge1, Answer("""{"action":"none","reason":"x"}"""));

        await ai.Gateway.AskAsync(Question("MARKET BRIEF", AiCatalog.AiTrader) with { SystemPrompt = AiCatalog.AiTraderPrompt }, new RecordingSink(), CancellationToken.None);

        var body = JsonNode.Parse(ai.Provider.Requests.Single().Body)!;
        Assert.Equal(AiCatalog.AiTraderPrompt, body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Null(body["tools"]);
    }

    // ---------- the tools ----------

    [Fact]
    public async Task A_tool_another_agent_holds_is_refused_in_words_and_never_runs()
    {
        // The Desk Assistant names the AI Trader's look, which asks a model itself: it is not one of its tools.
        var lookNow = new FakeTool(AiToolNames.AiTraderLookNow);
        var ai = Build(tools: [.. ChatToolsOf(AiCatalog.Agent(AiCatalog.DeskAssistant)!), lookNow]);
        ai.Provider.On(Judge1, Tools((AiToolNames.AiTraderLookNow, "{}")), Answer("I cannot ask the AI Trader."));

        var result = await ai.Gateway.AskAsync(Question("What would the AI Trader do now?"), new RecordingSink(), CancellationToken.None);

        var step = Assert.Single(result.Tools);
        Assert.False(step.Ok);
        Assert.Contains($"No tool named {AiToolNames.AiTraderLookNow}. The tools are: {AiToolNames.Runs}", step.Error);
        Assert.Equal(0, lookNow.Calls);
    }

    [Fact]
    public async Task A_tool_runs_for_the_call_that_asked_for_it()
    {
        var decisions = new FakeTool(AiToolNames.AiTraderDecisions);
        var ai = Build(tools: [.. ChatToolsOf(AiCatalog.Agent(AiCatalog.AiTrader)!).Where(t => t.Name != AiToolNames.AiTraderDecisions), decisions]);
        await ai.Store.SetAgentEnabledAsync(AiCatalog.AiTrader, true, "upendra", null);
        ai.Provider.On(Judge1, Tools((AiToolNames.AiTraderDecisions, """{"day":"2026-09-16"}""")), Answer("I bought at 10:20."));

        await ai.Gateway.AskAsync(Question("Why did you buy on 16 Sep?", AiCatalog.AiTrader) with { Source = "telegram" }, new RecordingSink(), CancellationToken.None);

        Assert.Equal(new AiToolCaller(AiCatalog.AiTrader, "telegram", "upendra", 1, "c-test"), decisions.LastArgs!.Caller);
        Assert.Equal("2026-09-16", decisions.LastArgs.String("day"));
    }

    [Fact]
    public async Task A_tool_that_waits_on_a_model_has_its_own_time_limit()
    {
        var slow = new SlowTool(AiToolNames.AiTraderLookNow, TimeSpan.FromSeconds(1));
        var ai = Build(Settings(s => s.ToolTimeoutSeconds = 0.2), null, [.. ChatToolsOf(AiCatalog.Agent(AiCatalog.AiTrader)!).Where(t => t.Name != slow.Name), slow]);
        await ai.Store.SetAgentEnabledAsync(AiCatalog.AiTrader, true, "upendra", null);
        ai.Provider.On(Judge1, Tools((AiToolNames.AiTraderLookNow, "{}")), Answer("I would wait."));

        var result = await ai.Gateway.AskAsync(Question("What would you do now?", AiCatalog.AiTrader), new RecordingSink(), CancellationToken.None);

        Assert.True(Assert.Single(result.Tools).Ok);
    }

    [Fact]
    public void Every_agent_the_owner_can_talk_with_has_a_persona_and_every_chat_tool_is_built_and_registered()
    {
        var built = typeof(AiController).Assembly.GetTypes()
            .Where(t => typeof(IAiTool).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .ToDictionary(t => ((IAiTool)RuntimeHelpers.GetUninitializedObject(t)).Name, t => t);
        string program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "AlgoTrading.Api", "Program.cs"));

        foreach (var agent in AiCatalog.Agents.Where(a => a.Built))
        {
            Assert.True(AiCatalog.CanChat(agent), $"{agent.Name} cannot be talked with");
            if (agent.Key != AiCatalog.DeskAssistant)
            {
                Assert.Contains("Rules for this chat", agent.ChatPrompt);
                Assert.Contains("never as JSON", agent.ChatPrompt);
                Assert.NotNull(agent.ChatTools);
                // Its persona names every tool it can read, so it knows what to call for what.
                Assert.All(agent.ChatTools!, name => Assert.Contains(name, agent.ChatPrompt));
            }

            foreach (string name in (agent.ChatTools ?? []).Concat(agent.Tools ?? []))
            {
                Assert.True(built.ContainsKey(name), $"No IAiTool named {name} ({agent.Name})");
                Assert.Contains($"AlgoTrading.Api.Services.AiTools.{built[name].Name}>()", program);
            }
        }

        Assert.False(AiCatalog.CanChat(AiCatalog.Agent("technical-analyst")!));
    }

    // ---------- the AI Trader's memory in a chat ----------

    [Fact]
    public async Task The_ai_trader_s_chat_reads_the_lessons_its_next_look_reads_and_none_learned_today()
    {
        // Monday 5 Oct 2026, 20:00 IST: today's live day has been reflected on, but its lesson is read from tomorrow.
        var now = IstTime.FromIst(new DateTime(2026, 10, 5, 20, 0, 0));
        var ai = Build();
        var gateway = new AiGateway(ai.Db, ai.Store, ai.Client, ai.Limiter, ai.Toolbox, ai.Health, ai.Options, NullLogger<AiGateway>.Instance,
            new AiAgentsTests.FixedTime(now));
        await ai.Store.SetAgentEnabledAsync(AiCatalog.AiTrader, true, "upendra", null);
        var yesterday = Lesson(ai.Db, "Wait for the first half hour before buying.", new DateOnly(2026, 10, 2));
        var today = Lesson(ai.Db, "Do not buy calls into a call wall.", new DateOnly(2026, 10, 5));
        var note = Note(ai.Db, "Explain each decision from the brief you had.", now.AddHours(-1));
        ai.Provider.On(Judge1, Answer("I read two memories."));

        var result = await gateway.AskAsync(Question("Which lessons are you using?", AiCatalog.AiTrader), new RecordingSink(), CancellationToken.None);

        string system = JsonNode.Parse(ai.Provider.Requests.Single().Body)!["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("Wait for the first half hour", system);
        Assert.Contains("Explain each decision", system);
        Assert.DoesNotContain("call wall", system);
        Assert.Equal(new[] { note.Id, yesterday.Id }, result.MemoryIds);
        Assert.DoesNotContain(today.Id, result.MemoryIds);
    }

    [Fact]
    public async Task The_other_agents_chats_read_their_memories_as_always()
    {
        var ai = Build(tools: ChatToolsOf(AiCatalog.Agent(AiCatalog.NewsAnalyst)!));
        await ai.Store.SetAgentEnabledAsync(AiCatalog.NewsAnalyst, true, "upendra", null);
        var note = Note(ai.Db, "A rating change is not results.", DateTime.UtcNow.AddDays(-1), AiCatalog.NewsAnalyst);
        ai.Provider.On(Extract1, Answer("ok"));

        var result = await ai.Gateway.AskAsync(Question("What moved in the last hour?", AiCatalog.NewsAnalyst), new RecordingSink(), CancellationToken.None);

        Assert.Equal(new[] { note.Id }, result.MemoryIds);
    }

    // ---------- the console's agent list ----------

    [Fact]
    public async Task The_agent_list_says_who_can_be_talked_with_and_what_each_reads_in_a_chat()
    {
        var all = AiCatalog.Agents.SelectMany(a => (a.ChatTools ?? []).Concat(a.Tools ?? [])).Distinct().Select(n => (IAiTool)new FakeTool(n)).ToArray();
        var ai = Build(tools: all);

        var list = (AiAgentList)((OkObjectResult)await ai.Controller().Agents(CancellationToken.None)).Value!;

        var trader = list.Agents.Single(a => a.Key == AiCatalog.AiTrader);
        Assert.True(trader.Chat);
        Assert.Empty(trader.Tools);
        Assert.Equal(AiToolNames.AiTraderChat, trader.ChatTools.Select(t => t.Name));
        var desk = list.Agents.Single(a => a.Key == AiCatalog.DeskAssistant);
        Assert.Equal(desk.Tools.Select(t => t.Name), desk.ChatTools.Select(t => t.Name));
        var planned = list.Agents.Single(a => a.Key == "macro-analyst");
        Assert.False(planned.Chat);
        Assert.Empty(planned.ChatTools);
    }

    // ---------- helpers ----------

    private static IAiTool[] ChatToolsOf(AiAgentDef agent) =>
        (agent.ChatTools ?? agent.Tools ?? []).Concat(AiToolNames.Desk).Distinct().Select(n => (IAiTool)new FakeTool(n)).ToArray();

    /// <summary>The first model of the agent's own tier's chain.</summary>
    private static string FirstOf(AiAgentDef agent) => AiCatalog.Tier(agent.Tier)!.DefaultChain[0];

    private static AiMemory Lesson(TradingDbContext db, string text, DateOnly learnedFrom)
    {
        var reflection = new AiReport
        {
            AgentKey = AiCatalog.AiTraderReflect, SubjectType = "day", SubjectId = $"day:{learnedFrom:yyyy-MM-dd}", SessionDate = learnedFrom,
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, Title = "Reflection",
        };
        db.AiReports.Add(reflection);
        db.SaveChanges();
        var m = new AiMemory
        {
            AgentKey = AiCatalog.AiTrader, Kind = AiMemoryKind.Lesson, Status = AiMemoryStatus.Active, Text = text, Source = AiMemorySource.Check,
            Via = "check", SourceReportId = reflection.Id, CreatedBy = "check", CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
            ActivatedUtc = DateTime.UtcNow,
        };
        db.AiMemories.Add(m);
        db.SaveChanges();
        return m;
    }

    private static AiMemory Note(TradingDbContext db, string text, DateTime createdUtc, string agent = AiCatalog.AiTrader)
    {
        var m = new AiMemory
        {
            AgentKey = agent, Kind = AiMemoryKind.Note, Status = AiMemoryStatus.Active, Text = text, Source = AiMemorySource.Owner,
            CreatedBy = "upendra", CreatedUtc = createdUtc, UpdatedUtc = createdUtc, ActivatedUtc = createdUtc,
        };
        db.AiMemories.Add(m);
        db.SaveChanges();
        return m;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlgoTrading.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }

    /// <summary>A tool that takes its time, with a limit of its own above it.</summary>
    private sealed class SlowTool(string name, TimeSpan takes) : IAiTool
    {
        public string Name => name;

        public string Description => "Slow.";

        public JsonObject Parameters => AiToolSchema.Object();

        public TimeSpan? Timeout => TimeSpan.FromSeconds(30);

        public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
        {
            await Task.Delay(takes, cancellationToken);
            return new AiToolOutput(new { ok = true }, null, 1, "done");
        }
    }
}
