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
    /// <summary>
    /// A word's bucket in the fake embeddings: FNV-1a, the same in every run. string.GetHashCode is randomised per
    /// process, so two words could share a bucket in one run and not the next, and a search test passed or failed by
    /// chance (10 Oct 2026: the Windows CI run ranked the wrong passage first).
    /// </summary>
    public static uint StableHash(string word)
    {
        uint h = 2166136261;
        foreach (char c in word)
        {
            h = (h ^ c) * 16777619;
        }

        return h;
    }

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
            // Off unless a test is about it: a pause per refusal would slow every fallback test.
            CapacityRetrySeconds = 0,
        };
        // Off unless a test is about it (on in production): every default chain would end in two Bedrock models.
        s.BedrockOpen.Enabled = false;
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

        public required AiModelHealth Health { get; init; }

        /// <summary>A scripted Bedrock Messages endpoint: nothing here reaches AWS.</summary>
        public required FakeClaude Claude { get; init; }

        public required BedrockClaudeClient ClaudeClient { get; init; }

        /// <summary>A scripted bedrock-runtime for Titan embeddings.</summary>
        public required FakeRuntime Runtime { get; init; }

        public required AiEmbedder Embedder { get; init; }

        /// <summary>A scripted Bedrock OpenAI-compatible endpoint for the open models, keyed by Bedrock's model id: nothing here reaches AWS.</summary>
        public required FakeProvider Open { get; init; }

        /// <summary>Signs with nothing: records each request it was asked to sign.</summary>
        public required FakeSigner Signer { get; init; }

        public required BedrockOpenChatClient OpenClient { get; init; }

        public AiController Controller(string user = "upendra")
        {
            var controller = new AiController(Db, Store, Gateway, Catalog, Limiter, Toolbox, Health, Options);
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
        var health = new AiModelHealth();
        var claude = new FakeClaude();
        var claudeClient = ClaudeClient(monitor, claude);
        var runtime = new FakeRuntime();
        var embedder = new AiEmbedder(client, monitor, new BedrockEmbeddingClient(monitor, runtime));
        var open = new FakeProvider();
        var signer = new FakeSigner();
        var openClient = new BedrockOpenChatClient(new Factory(open), monitor, signer);
        return new Services
        {
            Db = db,
            Provider = provider,
            Options = monitor,
            Store = store,
            Limiter = limiter,
            Client = client,
            Toolbox = toolbox,
            Health = health,
            Claude = claude,
            ClaudeClient = claudeClient,
            Runtime = runtime,
            Embedder = embedder,
            Open = open,
            Signer = signer,
            OpenClient = openClient,
            Catalog = new AiModelCatalog(client, monitor),
            Gateway = new AiGateway(db, store, client, limiter, toolbox, health, monitor, NullLogger<AiGateway>.Instance,
                router: new AiChatRouter(client, claudeClient, openClient)),
        };
    }

    /// <summary>
    /// A signer that adds a stand-in signature and keeps each request it signed, with the body it was given: what the
    /// client would hand AWSSDK.Core's signer. <see cref="FailWith"/> makes it fail as a machine with no credentials would.
    /// </summary>
    public sealed class FakeSigner : IBedrockRequestSigner
    {
        public List<(string Uri, string Region, string Body)> Signed { get; } = [];

        public AiAttemptFailedException? FailWith { get; set; }

        public Task SignAsync(HttpRequestMessage message, byte[] body, string region, CancellationToken cancellationToken)
        {
            if (FailWith is not null) throw FailWith;
            Signed.Add((message.RequestUri!.ToString(), region, Encoding.UTF8.GetString(body)));
            message.Headers.TryAddWithoutValidation("Authorization", $"AWS4-HMAC-SHA256 Credential=TEST/{Signed.Count}");
            return Task.CompletedTask;
        }
    }

    public const string Fable = "bedrock:anthropic.claude-fable-5-1";
    public const string Opus = "bedrock:anthropic.claude-opus-5-5";
    public const string Sonnet = "bedrock:anthropic.claude-sonnet-5-5";
    public const string Haiku45 = "bedrock:anthropic.claude-haiku-4-5";

    /// <summary>
    /// Anthropic's own SDK client for Bedrock, pointed at <paramref name="fake"/>: no AWS signing, no credentials, no
    /// network. What the desk's client parses is what the real endpoint would stream.
    /// </summary>
    public static BedrockClaudeClient ClaudeClient(IOptionsMonitor<AiSettings> monitor, FakeClaude fake) =>
        new(monitor, createClient: b => new Anthropic.Bedrock.AnthropicBedrockMantleClient(new Anthropic.Bedrock.MantleAwsClientOptions
        {
            AwsRegion = b.Region,
            SkipAuth = true,
            BaseUrl = "https://bedrock.test/anthropic",
            ClientOptions = new Anthropic.Core.ClientOptions
            {
                HttpClient = new HttpClient(fake, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan },
                MaxRetries = 0,
            },
        }));

    /// <summary>One streamed Messages API event: its SSE event name and its data.</summary>
    public sealed record ClaudeEvent(string Name, JsonObject Data);

    /// <summary>What one Claude model does when asked: streams these events, or answers a status.</summary>
    public abstract record ClaudeScript
    {
        public static ClaudeScript Stream(params ClaudeEvent[] events) => new ClaudeStream(events);

        public static ClaudeScript Status(int code, string body) => new ClaudeStatus(code, body);
    }

    public sealed record ClaudeStream(ClaudeEvent[] Events) : ClaudeScript;

    public sealed record ClaudeStatus(int Code, string Body) : ClaudeScript;

    /// <summary>A whole answer: an optional summarized thinking block (with its signature), then the text.</summary>
    public static ClaudeScript ClaudeAnswer(string text, string? thinking = "Weighing it.", string stop = "end_turn",
        int input = 100, int cacheRead = 0, int cacheWrite = 0, int output = 40)
    {
        var events = new List<ClaudeEvent> { MessageStart(input, cacheRead, cacheWrite) };
        int index = 0;
        if (thinking is not null) events.AddRange(ThinkingBlock(index++, thinking, "sig-" + index));
        if (text.Length > 0) events.AddRange(TextBlock(index, text));
        events.Add(MessageDelta(stop, output));
        events.Add(new ClaudeEvent("message_stop", new JsonObject { ["type"] = "message_stop" }));
        return ClaudeScript.Stream(events.ToArray());
    }

    public static ClaudeEvent MessageStart(int input = 100, int cacheRead = 0, int cacheWrite = 0) => new("message_start", new JsonObject
    {
        ["type"] = "message_start",
        ["message"] = new JsonObject
        {
            ["id"] = "msg_test",
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = "anthropic.claude-opus-5-5",
            ["content"] = new JsonArray(),
            ["stop_reason"] = null,
            ["stop_sequence"] = null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = input,
                ["cache_creation_input_tokens"] = cacheWrite,
                ["cache_read_input_tokens"] = cacheRead,
                ["output_tokens"] = 1,
            },
        },
    });

    public static ClaudeEvent[] ThinkingBlock(int index, string thinking, string signature) =>
    [
        new("content_block_start", new JsonObject
        {
            ["type"] = "content_block_start", ["index"] = index,
            ["content_block"] = new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "" },
        }),
        new("content_block_delta", new JsonObject
        {
            ["type"] = "content_block_delta", ["index"] = index,
            ["delta"] = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = thinking },
        }),
        new("content_block_delta", new JsonObject
        {
            ["type"] = "content_block_delta", ["index"] = index,
            ["delta"] = new JsonObject { ["type"] = "signature_delta", ["signature"] = signature },
        }),
        new("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = index }),
    ];

    /// <summary>A text block streamed in two pieces.</summary>
    public static ClaudeEvent[] TextBlock(int index, string text)
    {
        int half = text.Length / 2;
        return
        [
            new("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start", ["index"] = index,
                ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" },
            }),
            new("content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta", ["index"] = index,
                ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text[..half] },
            }),
            new("content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta", ["index"] = index,
                ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text[half..] },
            }),
            new("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = index }),
        ];
    }

    /// <summary>A tool_use block whose arguments' JSON arrives in two pieces.</summary>
    public static ClaudeEvent[] ToolUseBlock(int index, string id, string name, string arguments)
    {
        int half = arguments.Length / 2;
        return
        [
            new("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start", ["index"] = index,
                ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = new JsonObject() },
            }),
            new("content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta", ["index"] = index,
                ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = arguments[..half] },
            }),
            new("content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta", ["index"] = index,
                ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = arguments[half..] },
            }),
            new("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = index }),
        ];
    }

    public static ClaudeEvent MessageDelta(string stop, int output = 40, JsonObject? stopDetails = null)
    {
        var delta = new JsonObject { ["stop_reason"] = stop, ["stop_sequence"] = null };
        if (stopDetails is not null) delta["stop_details"] = stopDetails;
        return new ClaudeEvent("message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = delta,
            ["usage"] = new JsonObject { ["output_tokens"] = output },
        });
    }

    public static ClaudeEvent MessageStop() => new("message_stop", new JsonObject { ["type"] = "message_stop" });

    /// <summary>
    /// A scripted Bedrock Messages API: each model answers from its own queue, in order; one with nothing left answers
    /// 500, so an unexpected call shows. Every request body is kept, to pin what went over the wire.
    /// </summary>
    public sealed class FakeClaude : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<ClaudeScript>> _scripts = new(StringComparer.Ordinal);

        public List<(string Path, JsonObject Body)> Requests { get; } = [];

        /// <summary>Queues answers for <paramref name="model"/>, as Bedrock names it (no <c>bedrock:</c>).</summary>
        public FakeClaude On(string model, params ClaudeScript[] scripts)
        {
            model = AiCatalog.BedrockModelId(model);
            if (!_scripts.TryGetValue(model, out var queue)) _scripts[model] = queue = new Queue<ClaudeScript>();
            foreach (var s in scripts) queue.Enqueue(s);
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string text = await request.Content!.ReadAsStringAsync(cancellationToken);
            var body = JsonNode.Parse(text)!.AsObject();
            Requests.Add((request.RequestUri!.AbsolutePath, body));
            string model = body["model"]!.GetValue<string>();

            if (!_scripts.TryGetValue(model, out var queue) || queue.Count == 0)
            {
                return Json(500, """{"type":"error","error":{"type":"api_error","message":"no script"}}""");
            }

            switch (queue.Dequeue())
            {
                case ClaudeStatus s:
                    return Json(s.Code, s.Body);
                case ClaudeStream s:
                    string sse = string.Concat(s.Events.Select(e => $"event: {e.Name}\ndata: {e.Data.ToJsonString()}\n\n"));
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8) };
                    response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
                    return response;
                default:
                    throw new InvalidOperationException();
            }
        }

        private static HttpResponseMessage Json(int code, string body) =>
            new((HttpStatusCode)code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    /// <summary>
    /// A scripted bedrock-runtime for Titan: a 32-number word vector per text, so its vectors are told apart from the
    /// NVIDIA fake's 64. Every request body is kept.
    /// </summary>
    public sealed class FakeRuntime : IBedrockRuntimeInvoker
    {
        public List<(string Region, string Model, JsonObject Body)> Requests { get; } = [];

        /// <summary>Answer every call from this one on with this failure.</summary>
        public AiAttemptFailedException? FailWith { get; set; }

        /// <summary>Fail from this call on (1 = the first); 0 never.</summary>
        public int FailFrom { get; set; }

        public Task<string> InvokeAsync(string region, string modelId, string body, CancellationToken cancellationToken)
        {
            var json = JsonNode.Parse(body)!.AsObject();
            int n;
            lock (Requests)
            {
                Requests.Add((region, modelId, json));
                n = Requests.Count;
            }

            if (FailWith is not null && (FailFrom == 0 || n >= FailFrom)) throw FailWith;

            string text = json["inputText"]!.GetValue<string>();
            var v = new float[32];
            foreach (var word in text.ToLowerInvariant().Split((char[])[' ', '\n', '.', ',', '?', '›'], StringSplitOptions.RemoveEmptyEntries))
            {
                v[(int)(StableHash(word) % 32)] += 1;
            }

            var answer = new JsonObject
            {
                ["embedding"] = new JsonArray(v.Select(x => (JsonNode)x).ToArray()),
                ["inputTextTokenCount"] = 7,
                ["embeddingsByType"] = new JsonObject(),
            };
            return Task.FromResult(answer.ToJsonString());
        }
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

    public sealed record Seen(string Model, string Body, string? Authorization, string Path, string? Host = null);

    public sealed class FakeProvider : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<Script>> _scripts = new(StringComparer.Ordinal);

        public List<Seen> Requests { get; } = [];

        public string ModelsJson { get; set; } =
            """{"data":[{"id":"nvidia/nemotron-3-ultra-550b-a55b","owned_by":"nvidia"},{"id":"nvidia/nemotron-3-super-120b-a12b","owned_by":"nvidia"},{"id":"moonshotai/kimi-k3","owned_by":"moonshotai"},{"id":"z-ai/glm-5.3","owned_by":"z-ai"},{"id":"meta/llama-4-maverick","owned_by":"meta"},{"id":"nvidia/nemotron-3-embed-1b","owned_by":"nvidia"}]}""";

        /// <summary>The embeddings endpoint answers 503.</summary>
        public bool EmbeddingsFail { get; set; }

        /// <summary>A stand-in embedding: word counts hashed into 64 dimensions, so texts sharing words are close.</summary>
        public static float[] WordVector(string text)
        {
            var v = new float[64];
            foreach (var word in text.ToLowerInvariant().Split((char[])[' ', '\n', '.', ',', '?', '›'], StringSplitOptions.RemoveEmptyEntries))
            {
                v[(int)(StableHash(word) % 64)] += 1;
            }

            return v;
        }

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
            Requests.Add(new Seen(model, body, request.Headers.Authorization?.ToString(), path, request.RequestUri.Host));

            if (path.EndsWith("/embeddings", StringComparison.Ordinal))
            {
                if (EmbeddingsFail) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("busy") };
                var inputs = JsonNode.Parse(body)!["input"]!.AsArray().Select(i => i!.GetValue<string>()).ToList();
                var data = new JsonArray(inputs.Select((text, i) => (JsonNode)new JsonObject
                {
                    ["index"] = i,
                    ["embedding"] = new JsonArray(WordVector(text).Select(x => (JsonNode)x).ToArray()),
                }).ToArray());
                string json = new JsonObject { ["data"] = data, ["usage"] = new JsonObject { ["total_tokens"] = inputs.Count * 10 } }.ToJsonString();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }

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
