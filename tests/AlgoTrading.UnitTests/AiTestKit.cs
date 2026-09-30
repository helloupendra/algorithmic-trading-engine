using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AlgoTrading.UnitTests;

/// <summary>
/// A scripted NVIDIA endpoint and the AI services wired to it, for the gateway
/// and controller tests. Each model answers from its own queue of scripts, in
/// order; a model with no script left answers 500, so an unexpected call shows.
/// </summary>
internal static class AiTestKit
{
    public const string Key = "nvapi-TEST-SECRET-0123456789";

    public const string Judge1 = "nvidia/nemotron-3-ultra-550b-a55b";
    public const string Judge2 = "nvidia/nemotron-3-super-120b-a12b";
    public const string Judge3 = "moonshotai/kimi-k3";
    public const string Extract1 = "deepseek-ai/deepseek-v4.1-flash";

    public static TradingDbContext NewDb(string? name = null) =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString("N")).Options);

    public static AiSettings Settings(Action<AiSettings>? tweak = null)
    {
        var s = new AiSettings
        {
            ApiKey = Key,
            FirstTokenTimeoutSeconds = 5,
            IdleTimeoutSeconds = 5,
            AttemptTimeoutSeconds = 10,
        };
        tweak?.Invoke(s);
        return s;
    }

    /// <summary>A reasoning chunk, a content chunk, a finish, usage and [DONE]: a good streamed answer.</summary>
    public static Script Answer(string text, string reasoning = "Thinking.", int prompt = 20, int completion = 10) => Script.Sse(
        Chunk(reasoning: reasoning),
        Chunk(content: text, finish: "stop"),
        $$$"""{"choices":[],"usage":{"prompt_tokens":{{{prompt}}},"completion_tokens":{{{completion}}},"total_tokens":{{{prompt + completion}}}}}""",
        "[DONE]");

    public static string Chunk(string? content = null, string? reasoning = null, string? finish = null)
    {
        var delta = new JsonObject();
        if (content is not null) delta["content"] = content;
        if (reasoning is not null) delta["reasoning_content"] = reasoning;
        var choice = new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish };
        return new JsonObject { ["choices"] = new JsonArray(choice), ["usage"] = null }.ToJsonString();
    }

    public sealed class Services
    {
        public required TradingDbContext Db { get; init; }
        public required FakeProvider Provider { get; init; }
        public required Monitor Options { get; init; }
        public required AiSettingsStore Store { get; init; }
        public required AiGateway Gateway { get; init; }
        public required AiRateLimiter Limiter { get; init; }
        public required AiModelCatalog Catalog { get; init; }
        public required NvidiaChatClient Client { get; init; }

        public required AiToolbox Toolbox { get; init; }

        public AiController Controller(string user = "upendra")
        {
            var controller = new AiController(Db, Store, Gateway, Catalog, Limiter, Toolbox, Options);
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.Name, user), new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Role, "Admin")],
                        "test")),
                },
            };
            return controller;
        }
    }

    public static Services Build(AiSettings? settings = null, TradingDbContext? db = null, params IAiTool[] tools)
    {
        db ??= NewDb();
        var monitor = new Monitor(settings ?? Settings());
        var provider = new FakeProvider();
        var client = new NvidiaChatClient(new Factory(provider), monitor);
        var store = new AiSettingsStore(db);
        var limiter = new AiRateLimiter(monitor);
        var toolbox = new AiToolbox(tools);
        return new Services
        {
            Db = db,
            Provider = provider,
            Options = monitor,
            Store = store,
            Limiter = limiter,
            Client = client,
            Toolbox = toolbox,
            Catalog = new AiModelCatalog(client, monitor),
            Gateway = new AiGateway(db, store, client, limiter, toolbox, monitor, NullLogger<AiGateway>.Instance),
        };
    }

    /// <summary>A model round that asks for tools: one tool_calls chunk per call, then finish_reason tool_calls.</summary>
    public static Script Tools(params (string Name, string Args)[] calls) => Script.Sse(
        calls.Select((c, i) => new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["index"] = 0,
                ["delta"] = new JsonObject
                {
                    ["content"] = "",
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["index"] = i,
                        ["id"] = $"call-{c.Name}-{i}",
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args },
                    }),
                },
                ["finish_reason"] = i == calls.Length - 1 ? "tool_calls" : null,
            }),
        }.ToJsonString())
        .Append("""{"choices":[],"usage":{"prompt_tokens":100,"completion_tokens":5,"total_tokens":105}}""")
        .Append("[DONE]")
        .ToArray());

    /// <summary>A desk tool for tests: answers what it is given, counts its calls, or throws.</summary>
    public sealed class FakeTool(string name, Func<AiToolArgs, object>? answer = null, int rows = 1) : IAiTool
    {
        public int Calls { get; private set; }

        public AiToolArgs? LastArgs { get; private set; }

        public string Name => name;

        public string Description => $"Test tool {name}.";

        public JsonObject Parameters => AiToolSchema.Object(("runId", AiToolSchema.Integer("A run."), false));

        public Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
        {
            Calls++;
            LastArgs = args;
            object data = answer?.Invoke(args) ?? new { ok = true };
            return Task.FromResult(new AiToolOutput(data, new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc), rows, $"{rows} rows"));
        }
    }

    public static AiAskInput Question(string text = "What is max pain?", string agent = AiCatalog.DeskAssistant, string? tier = null) =>
        new(agent, tier, [new AiMessage("user", text)], null, 1024, 0.2, "c-test", "console", "upendra", 1);

    /// <summary>Records every event the gateway sends, in order.</summary>
    public sealed class RecordingSink : IAiStreamSink
    {
        public List<string> Events { get; } = [];

        public Func<string, ValueTask>? OnDelta { get; set; }

        public ValueTask StartAsync(long callId, IReadOnlyList<string> chain) { Events.Add($"start {callId}"); return ValueTask.CompletedTask; }

        public ValueTask AttemptAsync(string model, int number, int of, int round)
        {
            Events.Add(round == 1 ? $"attempt {model} {number}/{of}" : $"attempt {model} {number}/{of} round {round}");
            return ValueTask.CompletedTask;
        }

        public ValueTask ToolAsync(AiToolStep step) { Events.Add($"tool {step.Name} {(step.Ok ? "ok" : "error")}"); return ValueTask.CompletedTask; }

        public ValueTask ReasoningAsync(string text) { Events.Add($"reasoning {text}"); return ValueTask.CompletedTask; }

        public async ValueTask DeltaAsync(string text)
        {
            Events.Add($"delta {text}");
            if (OnDelta is not null) await OnDelta(text);
        }

        public ValueTask FallbackAsync(string model, string reason, string? next) { Events.Add($"fallback {model} -> {next}: {reason}"); return ValueTask.CompletedTask; }
    }

    public sealed class Monitor(AiSettings value) : IOptionsMonitor<AiSettings>
    {
        public AiSettings CurrentValue { get; set; } = value;

        public AiSettings Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<AiSettings, string?> listener) => null;
    }

    public sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>What one model does when asked.</summary>
    public abstract record Script
    {
        public static Script Sse(params string[] data) => new SseScript(data, Hang: false, Delay: TimeSpan.Zero);

        /// <summary>Streams these lines, then goes silent until the request is cancelled.</summary>
        public static Script SseThenHang(params string[] data) => new SseScript(data, Hang: true, Delay: TimeSpan.Zero);

        /// <summary>Sends nothing, not even headers, for this long.</summary>
        public static Script Silent(TimeSpan delay) => new SseScript(["[DONE]"], Hang: false, Delay: delay);

        public static Script Status(int code, string body = "") => new StatusScript(code, body);

        public static Script Unreachable() => new ThrowScript();
    }

    public sealed record SseScript(string[] Data, bool Hang, TimeSpan Delay) : Script;

    public sealed record StatusScript(int Code, string Body) : Script;

    public sealed record ThrowScript : Script;

    public sealed record Seen(string Model, string Body, string? Authorization, string Path);

    public sealed class FakeProvider : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<Script>> _scripts = new(StringComparer.Ordinal);

        public List<Seen> Requests { get; } = [];

        public string ModelsJson { get; set; } =
            """{"data":[{"id":"nvidia/nemotron-3-ultra-550b-a55b","owned_by":"nvidia"},{"id":"nvidia/nemotron-3-super-120b-a12b","owned_by":"nvidia"},{"id":"moonshotai/kimi-k3","owned_by":"moonshotai"},{"id":"z-ai/glm-5.3","owned_by":"z-ai"},{"id":"meta/llama-4-maverick","owned_by":"meta"},{"id":"nvidia/nemotron-3-embed-1b","owned_by":"nvidia"}]}""";

        public FakeProvider On(string model, params Script[] scripts)
        {
            if (!_scripts.TryGetValue(model, out var queue)) _scripts[model] = queue = new Queue<Script>();
            foreach (var s in scripts) queue.Enqueue(s);
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/models", StringComparison.Ordinal))
            {
                Requests.Add(new Seen(string.Empty, string.Empty, request.Headers.Authorization?.ToString(), path));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ModelsJson, Encoding.UTF8, "application/json") };
            }

            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            string model = JsonNode.Parse(body)!["model"]!.GetValue<string>();
            Requests.Add(new Seen(model, body, request.Headers.Authorization?.ToString(), path));

            if (!_scripts.TryGetValue(model, out var queue) || queue.Count == 0)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("no script") };
            }

            switch (queue.Dequeue())
            {
                case StatusScript s:
                    return new HttpResponseMessage((HttpStatusCode)s.Code) { Content = new StringContent(s.Body, Encoding.UTF8, "application/json") };
                case ThrowScript:
                    throw new HttpRequestException("connection refused", null, null);
                case SseScript s:
                    if (s.Delay > TimeSpan.Zero) await Task.Delay(s.Delay, cancellationToken);
                    string text = string.Concat(s.Data.Select(d => $"data: {d}\n\n"));
                    var stream = new ScriptedStream(Encoding.UTF8.GetBytes(text), s.Hang);
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
                    response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
                    return response;
                default:
                    throw new InvalidOperationException();
            }
        }
    }

    /// <summary>Gives its bytes, then either ends or waits for cancellation (a model gone silent mid-answer).</summary>
    private sealed class ScriptedStream(byte[] bytes, bool hang) : Stream
    {
        private int _at;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_at < bytes.Length)
            {
                int n = Math.Min(buffer.Length, bytes.Length - _at);
                bytes.AsMemory(_at, n).CopyTo(buffer);
                _at += n;
                return n;
            }

            if (hang) await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
