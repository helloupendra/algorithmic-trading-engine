using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Bedrock;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>
/// Claude on Amazon Bedrock: the Messages API at <c>bedrock-mantle.{region}.api.aws</c>, always streamed, through
/// Anthropic's SDK (<c>Anthropic.Bedrock</c>). The second provider beside <see cref="NvidiaChatClient"/>, with the same
/// contract, so a chain can name both and fall back from one to the other.
/// </summary>
/// <remarks>
/// <para>
/// No key: the SDK signs each request with the server's AWS credentials (the EC2 instance role, through the standard
/// AWS chain), which the desk never reads or stores. The SDK client is made on the first call, so a desk with Bedrock
/// off never looks for credentials.
/// </para>
/// <para>
/// The request follows what the current Claude models accept. Adaptive thinking, summarized so the console can show
/// it, steered by the tier's effort (<see cref="AiBedrockSettings.EffortByTier"/>); no temperature (the models reject
/// sampling settings); the tools offered with <c>tool_choice: auto</c>, or <c>none</c> once they are closed (a forced
/// choice is refused). The stable part of the system prompt and the tool definitions carry an explicit cache
/// breakpoint, so an agent's repeated calls read them from the prompt cache.
/// </para>
/// <para>
/// A tool loop is append-only. Each assistant turn goes back to the model that wrote it exactly as it came, its
/// thinking blocks and their signatures unchanged (<see cref="AiMessage.Replay"/>); another model gets the turn's
/// text and tool calls only, since thinking blocks are bound to the model that made them.
/// </para>
/// <para>
/// Every way a model can fail becomes an <see cref="AiAttemptFailedException"/>, as with NVIDIA: throttling, a busy
/// model or one not ready yet is a capacity refusal (429/503), so the gateway asks once more before moving on; a
/// model whose access is not enabled says so in words; a <c>refusal</c> stop is a failure, so the chain moves to its
/// next model (Bedrock has no server-side fallback). Neither credentials nor request bodies are logged or kept.
/// </para>
/// </remarks>
public sealed class BedrockClaudeClient : IAiChatProvider
{
    public const string HttpClientName = "ai-bedrock";

    private static readonly Regex AccountNumber = new(@"\b\d{12}\b", RegexOptions.CultureInvariant);
    private static readonly Regex UnsafeIdChars = new("[^A-Za-z0-9_-]", RegexOptions.CultureInvariant);

    private readonly IOptionsMonitor<AiSettings> _settings;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly Func<AiBedrockSettings, IAnthropicClient> _create;
    private readonly object _gate = new();
    private string? _region;
    private IAnthropicClient? _client;

    /// <param name="createClient">Makes the SDK client for a region; a test gives one that talks to a fake endpoint.</param>
    public BedrockClaudeClient(
        IOptionsMonitor<AiSettings> settings,
        IHttpClientFactory? httpClientFactory = null,
        Func<AiBedrockSettings, IAnthropicClient>? createClient = null)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
        _create = createClient ?? Create;
    }

    public string Name => AiCatalog.BedrockProviderName;

    public bool Serves(string model) => AiCatalog.IsBedrock(model);

    public string? Unavailable(AiSettings settings) =>
        settings.Bedrock.Enabled ? null : "Amazon Bedrock is off (Ai:Bedrock:Enabled is false)";

    public async Task<AiAttemptEnd> StreamAsync(
        string model,
        AiChatRequest request,
        Func<AiStreamPiece, ValueTask> onPiece,
        CancellationToken cancellationToken)
    {
        var s = _settings.CurrentValue;
        var firstToken = TimeSpan.FromSeconds(s.FirstTokenTimeoutSeconds);
        var idle = TimeSpan.FromSeconds(s.IdleTimeoutSeconds);
        var ceiling = TimeSpan.FromSeconds(s.AttemptTimeoutSeconds);
        var clock = Stopwatch.StartNew();
        bool started = false;

        using var timer = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);

        // As NvidiaChatClient: the first-token or the idle limit before every wait, never past the ceiling.
        void Arm()
        {
            var left = ceiling - clock.Elapsed;
            var limit = started ? idle : firstToken;
            timer.CancelAfter(left < limit ? (left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1)) : limit);
        }

        AiAttemptFailedException TimedOut() => clock.Elapsed >= ceiling - TimeSpan.FromMilliseconds(50)
            ? new AiAttemptFailedException($"timeout: still answering after {ceiling.TotalSeconds:0} s")
            : started
                ? new AiAttemptFailedException($"timeout: silent for {idle.TotalSeconds:0} s mid-answer")
                : new AiAttemptFailedException($"timeout: no answer within {firstToken.TotalSeconds:0} s");

        var parameters = Params(model, request, s.Bedrock);
        var blocks = new SortedDictionary<long, Block>();
        long input = 0, cacheRead = 0, cacheWrite = 0, output = 0;
        bool usageSeen = false;
        string? stopReason = null;
        string refusal = string.Empty;
        bool done = false;

        try
        {
            Arm();
            var client = Client(s.Bedrock);
            await using var events = client.Messages.CreateStreaming(parameters, linked.Token).GetAsyncEnumerator(linked.Token);
            while (true)
            {
                Arm();
                if (!await events.MoveNextAsync()) break;
                var e = events.Current;

                if (e.TryPickStart(out var start))
                {
                    var u = start.Message.Usage;
                    input = u.InputTokens;
                    cacheRead = u.CacheReadInputTokens ?? 0;
                    cacheWrite = u.CacheCreationInputTokens ?? 0;
                    output = u.OutputTokens;
                    usageSeen = true;
                }
                else if (e.TryPickContentBlockStart(out var opened))
                {
                    started = true;
                    var block = new Block();
                    var content = opened.ContentBlock;
                    if (content.TryPickText(out var text))
                    {
                        block.Kind = BlockKind.Text;
                        if (!string.IsNullOrEmpty(text.Text)) await Say(block, text.Text, onPiece);
                    }
                    else if (content.TryPickThinking(out var thinking))
                    {
                        block.Kind = BlockKind.Thinking;
                        block.Signature.Append(thinking.Signature);
                        if (!string.IsNullOrEmpty(thinking.Thinking)) await Think(block, thinking.Thinking, onPiece);
                    }
                    else if (content.TryPickRedactedThinking(out var redacted))
                    {
                        block.Kind = BlockKind.Redacted;
                        block.Text.Append(redacted.Data);
                    }
                    else if (content.TryPickToolUse(out var tool))
                    {
                        block.Kind = BlockKind.ToolUse;
                        block.Id = tool.ID;
                        block.Name = tool.Name;
                    }
                    else
                    {
                        block.Kind = BlockKind.Other;
                    }

                    blocks[opened.Index] = block;
                }
                else if (e.TryPickContentBlockDelta(out var delta))
                {
                    started = true;
                    if (!blocks.TryGetValue(delta.Index, out var block)) blocks[delta.Index] = block = new Block { Kind = BlockKind.Other };
                    var d = delta.Delta;
                    if (d.TryPickText(out var text))
                    {
                        if (block.Kind == BlockKind.Other) block.Kind = BlockKind.Text;
                        await Say(block, text.Text, onPiece);
                    }
                    else if (d.TryPickThinking(out var thinking))
                    {
                        await Think(block, thinking.Thinking, onPiece);
                    }
                    else if (d.TryPickSignature(out var signature))
                    {
                        block.Signature.Append(signature.Signature);
                    }
                    else if (d.TryPickInputJson(out var json))
                    {
                        block.Arguments.Append(json.PartialJson);
                    }
                }
                else if (e.TryPickDelta(out var ending))
                {
                    if (ending.Delta.StopReason is { } reason) stopReason = reason.Raw();
                    if (ending.Delta.StopDetails is { } details)
                    {
                        string category = details.Category is { } c ? c.Raw() : string.Empty;
                        refusal = string.Join(": ", new[] { category, OneLine(details.Explanation ?? string.Empty) }.Where(x => x.Length > 0));
                    }

                    var u = ending.Usage;
                    output = u.OutputTokens;
                    if (u.InputTokens is long i) input = i;
                    if (u.CacheReadInputTokens is long r) cacheRead = r;
                    if (u.CacheCreationInputTokens is long w) cacheWrite = w;
                    usageSeen = true;
                }
                else if (e.TryPickStop(out _))
                {
                    done = true;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw TimedOut();
        }
        catch (AnthropicApiException ex)
        {
            throw Failed(model, s.Bedrock.Region, (int)ex.StatusCode, ex.ResponseBody);
        }
        catch (AnthropicSseException ex)
        {
            throw StreamError(ex);
        }
        catch (AnthropicIOException ex)
        {
            throw new AiAttemptFailedException($"unreachable ({ex.InnerException?.HttpRequestError.ToString() ?? "connection"})");
        }
        catch (HttpRequestException ex)
        {
            throw new AiAttemptFailedException($"stream broke off ({ex.HttpRequestError})");
        }
        catch (IOException ex)
        {
            throw new AiAttemptFailedException($"stream broke off ({ex.GetType().Name})");
        }

        if (stopReason == "refusal")
        {
            throw new AiAttemptFailedException(refusal.Length > 0
                ? $"refused: the model's safeguards declined ({refusal})"
                : "refused: the model's safeguards declined");
        }

        if (!done && stopReason is null) throw new AiAttemptFailedException("stream broke off before the end");

        string finish = Finish(stopReason);
        var toolCalls = blocks.Values
            .Where(b => b.Kind == BlockKind.ToolUse && b.Name.Length > 0)
            .Select(b => new AiToolCall(b.Id, b.Name, Arguments(b)))
            .ToList();
        bool answered = blocks.Values.Any(b => b.Kind == BlockKind.Text && !string.IsNullOrWhiteSpace(b.Text.ToString()));

        // Thinking can take every token and leave nothing said; asking for a tool is an answer of its own.
        if (!answered && toolCalls.Count == 0)
        {
            throw new AiAttemptFailedException(finish == "length" ? "empty answer: ran out of tokens while reasoning" : "empty answer");
        }

        AiUsage? usage = usageSeen
            ? new AiUsage(Int(input + cacheRead + cacheWrite), Int(output), Int(input + cacheRead + cacheWrite + output))
            {
                CacheReadTokens = Int(cacheRead),
                CacheWriteTokens = Int(cacheWrite),
            }
            : null;

        return new AiAttemptEnd(finish, usage, toolCalls) { Replay = Replay(blocks.Values) };
    }

    /// <summary>
    /// The SDK request for one model. Public so a test can pin what goes over the wire: no temperature, adaptive
    /// thinking with the tier's effort, <c>tool_choice</c> auto or none, and the cache breakpoints.
    /// </summary>
    public static MessageCreateParams Params(string model, AiChatRequest request, AiBedrockSettings bedrock)
    {
        var parameters = new MessageCreateParams
        {
            Model = AiCatalog.BedrockModelId(model),
            // Thinking counts against the limit: room for it beyond the caller's answer (AiBedrockSettings.MinMaxTokens).
            MaxTokens = Math.Max(request.MaxTokens, bedrock.MinMaxTokens),
            Messages = Messages(model, request.Messages),
        };

        if (SystemBlocks(request) is { Count: > 0 } system) parameters = parameters with { System = system };

        // Haiku 4.5 takes neither adaptive thinking nor effort; every other model here thinks adaptively, and on
        // Fable 5.1 and Opus 5.5 thinking cannot be turned off at all.
        bool adaptive = AiCatalog.BedrockModel(model)?.AdaptiveThinking
            ?? !AiCatalog.BedrockModelId(model).Contains("haiku-4-5", StringComparison.Ordinal);
        if (adaptive)
        {
            parameters = parameters with
            {
                Thinking = new ThinkingConfigAdaptive { Display = Display.Summarized },
                OutputConfig = new OutputConfig { Effort = Effort(request.Tier, bedrock) },
            };
        }

        if (request.Tools is { Count: > 0 } specs)
        {
            var tools = new List<ToolUnion>();
            for (int i = 0; i < specs.Count; i++)
            {
                var tool = new Tool { Name = specs[i].Name, Description = specs[i].Description, InputSchema = Schema(specs[i].Parameters) };
                // The tools come first in the prompt: a breakpoint on the last caches all of them.
                if (i == specs.Count - 1) tool = tool with { CacheControl = new CacheControlEphemeral() };
                tools.Add(tool);
            }

            parameters = parameters with
            {
                Tools = tools,
                ToolChoice = request.ToolsClosed ? new ToolChoiceNone() : new ToolChoiceAuto(),
            };
        }

        return parameters;
    }

    /// <summary>The tier's effort: the setting's word, medium for a tier it does not name, low for a model's health test.</summary>
    public static Effort Effort(string? tier, AiBedrockSettings bedrock)
    {
        if (tier is null) return Anthropic.Models.Messages.Effort.Low;
        string word = bedrock.EffortByTier.TryGetValue(tier, out string? w) ? w.Trim().ToLowerInvariant() : "medium";
        return word switch
        {
            "low" => Anthropic.Models.Messages.Effort.Low,
            "high" => Anthropic.Models.Messages.Effort.High,
            "xhigh" => Anthropic.Models.Messages.Effort.Xhigh,
            "max" => Anthropic.Models.Messages.Effort.Max,
            _ => Anthropic.Models.Messages.Effort.Medium,
        };
    }

    /// <summary>
    /// The system prompt as blocks: the part every call of the agent shares, with a cache breakpoint, then the part
    /// that changes (memories, the time). None for an empty prompt.
    /// </summary>
    private static List<TextBlockParam> SystemBlocks(AiChatRequest request)
    {
        string text = request.SystemPrompt ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return [];

        int cut = request.StableSystemChars > 0 && request.StableSystemChars < text.Length ? request.StableSystemChars : text.Length;
        if (string.IsNullOrWhiteSpace(text[..cut])) cut = text.Length;
        var blocks = new List<TextBlockParam> { new() { Text = text[..cut], CacheControl = new CacheControlEphemeral() } };
        if (!string.IsNullOrWhiteSpace(text[cut..])) blocks.Add(new TextBlockParam { Text = text[cut..] });
        return blocks;
    }

    /// <summary>
    /// The conversation in Anthropic's shape: a tool's answer is a <c>tool_result</c> block in a user turn, and turns
    /// of the same role in a row are merged, since the API alternates user and assistant.
    /// </summary>
    private static List<MessageParam> Messages(string model, IReadOnlyList<AiMessage> messages)
    {
        var turns = new List<(bool User, List<ContentBlockParam> Blocks)>();
        foreach (var m in messages)
        {
            bool user = m.Role != "assistant";
            List<ContentBlockParam> content;
            if (m.Role == "tool")
            {
                content = [new ToolResultBlockParam
                {
                    ToolUseID = SafeId(m.ToolCallId ?? string.Empty),
                    Content = string.IsNullOrEmpty(m.Content) ? "(no output)" : m.Content,
                }];
            }
            else if (!user && m.Replay is { } replay && replay.Model == model)
            {
                // The same model reads its own turn back exactly as it wrote it, thinking and all.
                content = replay.Blocks.Select(ToParam).Where(b => b is not null).Select(b => b!).ToList();
            }
            else if (!user)
            {
                content = [];
                if (!string.IsNullOrWhiteSpace(m.Content)) content.Add(new TextBlockParam { Text = m.Content.TrimEnd() });
                foreach (var call in m.ToolCalls ?? [])
                {
                    content.Add(new ToolUseBlockParam { ID = SafeId(call.Id), Name = call.Name, Input = Input(call.Arguments) });
                }
            }
            else
            {
                content = string.IsNullOrWhiteSpace(m.Content) ? [] : [new TextBlockParam { Text = m.Content }];
            }

            if (content.Count == 0) continue;
            if (turns.Count > 0 && turns[^1].User == user) turns[^1].Blocks.AddRange(content);
            else turns.Add((user, content));
        }

        return turns.Select(t => new MessageParam { Role = t.User ? Role.User : Role.Assistant, Content = t.Blocks }).ToList();
    }

    private static ContentBlockParam? ToParam(AiReplayBlock block) => block switch
    {
        AiReplayBlock.Thinking t => new ThinkingBlockParam { Thinking = t.Reasoning, Signature = t.Signature },
        AiReplayBlock.RedactedThinking r => new RedactedThinkingBlockParam { Data = r.Data },
        AiReplayBlock.Text { Value.Length: > 0 } x when !string.IsNullOrWhiteSpace(x.Value) => new TextBlockParam { Text = x.Value },
        AiReplayBlock.ToolUse u => new ToolUseBlockParam { ID = SafeId(u.Id), Name = u.Name, Input = Input(u.Arguments) },
        _ => null,
    };

    /// <summary>
    /// A tool call's id as Anthropic accepts it (letters, digits, <c>_</c> and <c>-</c>): an NVIDIA model's call,
    /// handed to Claude after a fallback, may carry others. The same id always maps the same way, so its result still
    /// matches.
    /// </summary>
    public static string SafeId(string id) => id.Length == 0 ? "call" : UnsafeIdChars.Replace(id, "_");

    private static IReadOnlyDictionary<string, JsonElement> Input(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return new Dictionary<string, JsonElement>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments) ?? new Dictionary<string, JsonElement>();
        }
        catch (JsonException)
        {
            // A model that wrote broken arguments was told so by the gateway; the call is kept, its input empty.
            return new Dictionary<string, JsonElement>();
        }
    }

    private static InputSchema Schema(JsonObject parameters) =>
        InputSchema.FromRawUnchecked(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parameters.ToJsonString())!);

    /// <summary>Claude's stop reason in the words the rest of the desk uses (the NVIDIA side's): stop, length, tool_calls.</summary>
    public static string Finish(string? stopReason) => stopReason switch
    {
        "end_turn" or "stop_sequence" => "stop",
        "max_tokens" or "model_context_window_exceeded" => "length",
        "tool_use" => "tool_calls",
        null => string.Empty,
        _ => stopReason,
    };

    /// <summary>
    /// What an HTTP refusal from Bedrock means, for the attempts table. Throttling, a busy model and a model not ready
    /// yet are capacity (429/503), so the gateway asks once more; a model whose access is not enabled says so.
    /// Public so a test can pin each case.
    /// </summary>
    public static AiAttemptFailedException Failed(string model, string region, int status, string? body)
    {
        string detail = Detail(body);
        string id = AiCatalog.BedrockModelId(model);
        bool credentials = detail.Contains("security token", StringComparison.OrdinalIgnoreCase)
                           || detail.Contains("signature", StringComparison.OrdinalIgnoreCase)
                           || detail.Contains("credential", StringComparison.OrdinalIgnoreCase);

        if (status == 401 || (status == 403 && credentials))
        {
            return new AiAttemptFailedException($"http {status}: Bedrock refused the server's AWS credentials{Tail(detail)}", status);
        }

        if (status is 403 or 404
            || detail.Contains("access", StringComparison.OrdinalIgnoreCase) && detail.Contains("model", StringComparison.OrdinalIgnoreCase)
               && status == 400)
        {
            return new AiAttemptFailedException(
                $"http {status}: Bedrock model access is not enabled for {id} in {region}{Tail(detail)}", status);
        }

        bool notReady = detail.Contains("ModelNotReady", StringComparison.OrdinalIgnoreCase)
                        || detail.Contains("not ready", StringComparison.OrdinalIgnoreCase);
        bool throttled = status == 429 || detail.Contains("throttl", StringComparison.OrdinalIgnoreCase);
        if (throttled) return new AiAttemptFailedException($"http {status}: throttled by Bedrock{Tail(detail)}", 429);
        if (notReady) return new AiAttemptFailedException($"http {status}: model not ready on Bedrock, temporarily{Tail(detail)}", 503);
        if (status is 502 or 503 or 529 || detail.Contains("ServiceUnavailable", StringComparison.OrdinalIgnoreCase))
        {
            return new AiAttemptFailedException($"http {status}: Bedrock is overloaded{Tail(detail)}", status == 529 ? 503 : status);
        }

        return new AiAttemptFailedException(detail.Length > 0 ? $"http {status}: {detail}" : $"http {status}", status);
    }

    private static AiAttemptFailedException StreamError(AnthropicSseException ex)
    {
        string detail = OneLine(Mask(ex.Message));
        return ex.ErrorType switch
        {
            ErrorType.OverloadedError => new AiAttemptFailedException($"provider error: overloaded{Tail(detail)}", 503),
            ErrorType.RateLimitError => new AiAttemptFailedException($"provider error: throttled{Tail(detail)}", 429),
            _ => new AiAttemptFailedException($"provider error: {detail}"),
        };
    }

    private static string Tail(string detail) => detail.Length > 0 ? $": {detail}" : string.Empty;

    /// <summary>The provider's own words for a refusal, short, on one line, the AWS account number masked.</summary>
    private static string Detail(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        string text = body;
        try
        {
            var node = JsonNode.Parse(body);
            text = node?["error"]?["message"]?.ToString() ?? node?["message"]?.ToString() ?? node?["Message"]?.ToString()
                ?? node?["error"]?.ToString() ?? body;
        }
        catch (JsonException)
        {
            // Not JSON: keep the text.
        }

        return OneLine(Mask(text));
    }

    private static string Mask(string text) => AccountNumber.Replace(text, "[account]");

    private static string OneLine(string text)
    {
        string flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.StartsWith('<')) return string.Empty;
        return flat.Length <= 200 ? flat : flat[..197] + "...";
    }

    private static string Arguments(Block b) => b.Arguments.Length > 0 ? b.Arguments.ToString() : "{}";

    private static IReadOnlyList<AiReplayBlock> Replay(IEnumerable<Block> blocks) => blocks
        .Select(b => b.Kind switch
        {
            BlockKind.Thinking => (AiReplayBlock)new AiReplayBlock.Thinking(b.Text.ToString(), b.Signature.ToString()),
            BlockKind.Redacted => new AiReplayBlock.RedactedThinking(b.Text.ToString()),
            BlockKind.Text => new AiReplayBlock.Text(b.Text.ToString()),
            BlockKind.ToolUse => new AiReplayBlock.ToolUse(b.Id, b.Name, Arguments(b)),
            _ => null,
        })
        .Where(b => b is not null)
        .Select(b => b!)
        .ToList();

    private static int? Int(long value) => (int)Math.Min(int.MaxValue, Math.Max(0, value));

    private static async ValueTask Say(Block block, string text, Func<AiStreamPiece, ValueTask> onPiece)
    {
        block.Text.Append(text);
        if (text.Length > 0) await Deliver(onPiece, new AiStreamPiece.Content(text));
    }

    private static async ValueTask Think(Block block, string text, Func<AiStreamPiece, ValueTask> onPiece)
    {
        block.Text.Append(text);
        if (text.Length > 0) await Deliver(onPiece, new AiStreamPiece.Reasoning(text));
    }

    /// <summary>As NvidiaChatClient's: a failure on the far side is the caller's, never the model's.</summary>
    private static async ValueTask Deliver(Func<AiStreamPiece, ValueTask> onPiece, AiStreamPiece piece)
    {
        try
        {
            await onPiece(piece);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AiSinkFailedException(ex);
        }
    }

    /// <summary>The SDK client for the region in the settings, made once and again only when the region changes.</summary>
    private IAnthropicClient Client(AiBedrockSettings bedrock)
    {
        lock (_gate)
        {
            if (_client is null || _region != bedrock.Region)
            {
                _client = _create(bedrock);
                _region = bedrock.Region;
            }

            return _client;
        }
    }

    /// <summary>
    /// The production client: SigV4 with the standard AWS chain (the instance role on EC2). No retries of its own:
    /// the gateway retries a capacity refusal once and then moves down the chain, and keeps the time limits.
    /// </summary>
    private IAnthropicClient Create(AiBedrockSettings bedrock) => new AnthropicBedrockMantleClient(new MantleAwsClientOptions
    {
        AwsRegion = bedrock.Region,
        ClientOptions = new ClientOptions
        {
            HttpClient = _httpClientFactory?.CreateClient(HttpClientName) ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
            MaxRetries = 0,
            Timeout = TimeSpan.FromSeconds(_settings.CurrentValue.AttemptTimeoutSeconds + 30),
        },
    });

    private enum BlockKind
    {
        Other,
        Text,
        Thinking,
        Redacted,
        ToolUse,
    }

    private sealed class Block
    {
        public BlockKind Kind;
        public string Id = string.Empty;
        public string Name = string.Empty;
        public readonly StringBuilder Text = new();
        public readonly StringBuilder Signature = new();
        public readonly StringBuilder Arguments = new();
    }
}
