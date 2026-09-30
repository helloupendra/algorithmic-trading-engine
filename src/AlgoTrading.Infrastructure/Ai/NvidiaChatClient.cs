using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>
/// One message of a conversation: <c>user</c>, <c>assistant</c> or <c>tool</c>.
/// An assistant message may carry the tools the model asked for; a tool
/// message answers one of them by its id.
/// </summary>
public sealed record AiMessage(string Role, string Content, IReadOnlyList<AiToolCall>? ToolCalls = null, string? ToolCallId = null);

/// <summary>A tool a model asked to run: its id (echoed on the answer), its name and its arguments as the JSON text the model wrote.</summary>
public sealed record AiToolCall(string Id, string Name, string Arguments);

/// <summary>A tool offered to a model: a name, what it does, and its arguments as a JSON schema.</summary>
public sealed record AiToolSpec(string Name, string Description, JsonObject Parameters);

/// <summary>What is asked of a model, whichever model of the chain it goes to.</summary>
/// <param name="Tools">The tools the model may ask for; none when empty.</param>
/// <param name="ToolsClosed">
/// The tools stay described (the conversation already holds their calls) but
/// the model may not ask for another: the last round of a question that used
/// its allowance.
/// </param>
public sealed record AiChatRequest(
    IReadOnlyList<AiMessage> Messages,
    string SystemPrompt,
    int MaxTokens,
    double Temperature,
    IReadOnlyList<AiToolSpec>? Tools = null,
    bool ToolsClosed = false);

/// <summary>Tokens a call used, as the provider counted them.</summary>
public sealed record AiUsage(int? PromptTokens, int? CompletionTokens, int? TotalTokens);

/// <summary>A piece of a streamed answer.</summary>
public abstract record AiStreamPiece
{
    /// <summary>Part of the model's reasoning (Nemotron streams it before the answer).</summary>
    public sealed record Reasoning(string Text) : AiStreamPiece;

    /// <summary>Part of the answer.</summary>
    public sealed record Content(string Text) : AiStreamPiece;
}

/// <summary>How a model's answer ended: with an answer, or with the tools it wants run first.</summary>
public sealed record AiAttemptEnd(string FinishReason, AiUsage? Usage, IReadOnlyList<AiToolCall> ToolCalls)
{
    public AiAttemptEnd(string finishReason, AiUsage? usage) : this(finishReason, usage, []) { }
}

/// <summary>A model that did not answer, and the reason the next one is tried.</summary>
public sealed class AiAttemptFailedException : Exception
{
    public AiAttemptFailedException(string outcome, int? httpStatus = null) : base(outcome)
    {
        Outcome = outcome;
        HttpStatus = httpStatus;
    }

    /// <summary>Short, for the attempts table: <c>timeout</c>, <c>http 429</c>, <c>unreachable</c>...</summary>
    public string Outcome { get; }

    public int? HttpStatus { get; }
}

/// <summary>The caller's side failed while a model was answering: not the model's fault, so no fallback.</summary>
public sealed class AiSinkFailedException(Exception inner) : Exception("The receiver of the answer failed.", inner);

/// <summary>A model in the provider's catalog.</summary>
public sealed record ProviderModel(string Id, string OwnedBy);

/// <summary>
/// The desk's one door to NVIDIA's hosted models: an OpenAI-compatible chat
/// endpoint, always streamed, and the model list.
/// </summary>
/// <remarks>
/// <para>
/// Streaming is not a nicety here. Behind Cloudflare an origin that sends
/// nothing for 100 seconds is cut off, and the Judge tier's first model can
/// think for longer than that; a stream sends bytes from the first token.
/// It also lets the caller tell a model that never started from one that
/// stalled mid-answer, which are different timeouts
/// (<see cref="AiSettings.FirstTokenTimeoutSeconds"/>,
/// <see cref="AiSettings.IdleTimeoutSeconds"/>) under one ceiling
/// (<see cref="AiSettings.AttemptTimeoutSeconds"/>).
/// </para>
/// <para>
/// Every way a model can fail becomes an <see cref="AiAttemptFailedException"/>
/// with a short outcome, so the caller moves on to the next model; only the
/// caller's own cancellation escapes as <see cref="OperationCanceledException"/>.
/// The key goes in the Authorization header and nowhere else: no outcome,
/// log line or exception carries it.
/// </para>
/// </remarks>
public sealed class NvidiaChatClient
{
    public const string HttpClientName = "ai-provider";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AiSettings> _settings;

    public NvidiaChatClient(IHttpClientFactory httpClientFactory, IOptionsMonitor<AiSettings> settings)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
    }

    public string ChatEndpoint => $"{BaseUrl}/chat/completions";

    private string BaseUrl => (_settings.CurrentValue.BaseUrl is { Length: > 0 } b ? b : AiCatalog.DefaultBaseUrl).TrimEnd('/');

    /// <summary>
    /// Streams one model's answer into <paramref name="onPiece"/>, and says how
    /// it ended. Throws <see cref="AiAttemptFailedException"/> when the model
    /// fails in any way the next model might not.
    /// </summary>
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

        // Before every wait: the first-token or the idle limit, whichever
        // applies, but never past the ceiling.
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

        using var message = new HttpRequestMessage(HttpMethod.Post, ChatEndpoint)
        {
            Content = new StringContent(RequestBody(model, request), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var http = _httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        Arm();
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw TimedOut();
        }
        catch (HttpRequestException ex)
        {
            throw new AiAttemptFailedException($"unreachable ({ex.HttpRequestError})");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                string detail = await ErrorDetailAsync(response, linked.Token);
                throw new AiAttemptFailedException(detail.Length > 0 ? $"http {code}: {detail}" : $"http {code}", code);
            }

            string finish = string.Empty;
            AiUsage? usage = null;
            bool done = false;
            bool answered = false;
            var calls = new SortedDictionary<int, (string? Id, StringBuilder Name, StringBuilder Args)>();

            try
            {
                await using var body = await response.Content.ReadAsStreamAsync(linked.Token);
                using var reader = new StreamReader(body, Encoding.UTF8);
                while (true)
                {
                    Arm();
                    string? line = await reader.ReadLineAsync(linked.Token);
                    if (line is null) break;
                    if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                    string data = line[5..].Trim();
                    if (data == "[DONE]")
                    {
                        done = true;
                        break;
                    }

                    var chunk = ParseChunk(data);
                    if (chunk.Error is not null) throw new AiAttemptFailedException($"provider error: {chunk.Error}");
                    if (chunk.Usage is not null) usage = chunk.Usage;
                    if (chunk.FinishReason is { Length: > 0 }) finish = chunk.FinishReason;

                    if (chunk.Reasoning is { Length: > 0 })
                    {
                        started = true;
                        await Deliver(onPiece, new AiStreamPiece.Reasoning(chunk.Reasoning));
                    }

                    if (chunk.Content is { Length: > 0 })
                    {
                        started = true;
                        answered |= !string.IsNullOrWhiteSpace(chunk.Content);
                        await Deliver(onPiece, new AiStreamPiece.Content(chunk.Content));
                    }

                    // A tool call arrives in pieces by index: the id and name
                    // first, the arguments' JSON text in any number of parts.
                    foreach (var part in chunk.ToolCalls)
                    {
                        started = true;
                        if (!calls.TryGetValue(part.Index, out var call))
                        {
                            call = (null, new StringBuilder(), new StringBuilder());
                        }

                        if (part.Id is { Length: > 0 }) call.Id = part.Id;
                        if (part.Name is { Length: > 0 }) call.Name.Append(part.Name);
                        if (part.Arguments is { Length: > 0 }) call.Args.Append(part.Arguments);
                        calls[part.Index] = call;
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw TimedOut();
            }
            catch (IOException ex)
            {
                throw new AiAttemptFailedException($"stream broke off ({ex.GetType().Name})");
            }
            catch (HttpRequestException ex)
            {
                throw new AiAttemptFailedException($"stream broke off ({ex.HttpRequestError})");
            }

            if (!done && finish.Length == 0) throw new AiAttemptFailedException("stream broke off before the end");

            var toolCalls = calls
                .Where(c => c.Value.Name.Length > 0)
                .Select(c => new AiToolCall(c.Value.Id ?? $"call-{c.Key}", c.Value.Name.ToString(), c.Value.Args.ToString()))
                .ToList();

            // A reasoning model can spend every token thinking and say nothing.
            // Asking for a tool is an answer of its own.
            if (!answered && toolCalls.Count == 0)
            {
                throw new AiAttemptFailedException(finish == "length"
                    ? "empty answer: ran out of tokens while reasoning"
                    : "empty answer");
            }

            return new AiAttemptEnd(finish, usage, toolCalls);
        }
    }

    /// <summary>
    /// Hands a piece on. A failure on the far side (the console's connection
    /// closed mid-write) is wrapped, so the read loop's own catches cannot
    /// mistake it for the model's stream breaking and ask the next model.
    /// </summary>
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

    /// <summary>
    /// Vectors for <paramref name="inputs"/>, in order, from an embedding model:
    /// <paramref name="inputType"/> is <c>passage</c> for what is stored and
    /// <c>query</c> for what is searched with (the model embeds the two
    /// differently). Throws <see cref="AiAttemptFailedException"/> on any failure.
    /// </summary>
    public async Task<(IReadOnlyList<float[]> Vectors, int? Tokens)> EmbedAsync(
        string model, IReadOnlyList<string> inputs, string inputType, CancellationToken cancellationToken)
    {
        var s = _settings.CurrentValue;
        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = new JsonArray(inputs.Select(i => (JsonNode)i).ToArray()),
            ["input_type"] = inputType,
            ["encoding_format"] = "float",
            ["truncate"] = "END",
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/embeddings")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ApiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var response = await _httpClientFactory.CreateClient(HttpClientName).SendAsync(message, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                string detail = await ErrorDetailAsync(response, timeout.Token);
                throw new AiAttemptFailedException(detail.Length > 0 ? $"http {code}: {detail}" : $"http {code}", code);
            }

            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var data = root?["data"] as JsonArray ?? throw new AiAttemptFailedException("no vectors in the answer");
            var vectors = data
                .OrderBy(d => d?["index"]?.GetValue<int>() ?? 0)
                .Select(d => (d?["embedding"] as JsonArray ?? []).Select(x => x!.GetValue<float>()).ToArray())
                .ToList();
            if (vectors.Count != inputs.Count || vectors.Any(v => v.Length == 0)) throw new AiAttemptFailedException("the answer's vectors do not match the inputs");
            return (vectors, Int(root?["usage"]?["total_tokens"]));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiAttemptFailedException("timeout");
        }
        catch (HttpRequestException ex)
        {
            throw new AiAttemptFailedException($"unreachable ({ex.HttpRequestError})");
        }
        catch (JsonException)
        {
            throw new AiAttemptFailedException("unreadable answer");
        }
    }

    /// <summary>The provider's model list. Throws <see cref="AiAttemptFailedException"/> when it cannot be read.</summary>
    public async Task<IReadOnlyList<ProviderModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var s = _settings.CurrentValue;
        using var message = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ApiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await _httpClientFactory.CreateClient(HttpClientName).SendAsync(message, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new AiAttemptFailedException($"http {(int)response.StatusCode}", (int)response.StatusCode);

            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var data = root?["data"] as JsonArray ?? [];
            return data
                .Select(m => new ProviderModel(m?["id"]?.GetValue<string>() ?? string.Empty, m?["owned_by"]?.GetValue<string>() ?? string.Empty))
                .Where(m => m.Id.Length > 0)
                .ToList();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiAttemptFailedException("timeout");
        }
        catch (HttpRequestException ex)
        {
            throw new AiAttemptFailedException($"unreachable ({ex.HttpRequestError})");
        }
        catch (JsonException)
        {
            throw new AiAttemptFailedException("unreadable model list");
        }
    }

    /// <summary>The JSON body sent for one model. Public so a test can pin what goes over the wire.</summary>
    public static string RequestBody(string model, AiChatRequest request)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt });
        }

        foreach (var m in request.Messages)
        {
            if (m.ToolCalls is { Count: > 0 } asked)
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = string.IsNullOrWhiteSpace(m.Content) ? null : m.Content,
                    ["tool_calls"] = new JsonArray(asked.Select(c => (JsonNode)new JsonObject
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments.Length > 0 ? c.Arguments : "{}" },
                    }).ToArray()),
                });
            }
            else if (m.Role == "tool")
            {
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId, ["content"] = m.Content });
            }
            else
            {
                messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
            }
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = request.MaxTokens,
            ["temperature"] = request.Temperature,
            ["stream"] = true,
            // Without it a stream never says how many tokens it used.
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };

        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.Parameters.DeepClone(),
                },
            }).ToArray());
            body["tool_choice"] = request.ToolsClosed ? "none" : "auto";
        }

        return body.ToJsonString();
    }

    /// <summary>One <c>data:</c> line of the stream, read leniently: a field that is missing or of another type is skipped.</summary>
    public static StreamChunk ParseChunk(string data)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(data);
        }
        catch (JsonException)
        {
            return new StreamChunk(null, null, null, null, null);
        }

        if (root is not JsonObject obj) return new StreamChunk(null, null, null, null, null);

        if (obj["error"] is JsonNode err)
        {
            string text = err is JsonObject e ? e["message"]?.ToString() ?? err.ToJsonString() : err.ToString();
            return new StreamChunk(null, null, null, null, OneLine(text));
        }

        string? content = null, reasoning = null, finish = null;
        var toolCalls = new List<ToolCallPart>();
        if (obj["choices"] is JsonArray { Count: > 0 } choices && choices[0] is JsonObject choice)
        {
            if (choice["delta"] is JsonObject delta)
            {
                content = Str(delta["content"]);
                // NVIDIA names it reasoning_content; some servers say reasoning.
                reasoning = Str(delta["reasoning_content"]) ?? Str(delta["reasoning"]);

                if (delta["tool_calls"] is JsonArray parts)
                {
                    for (int i = 0; i < parts.Count; i++)
                    {
                        if (parts[i] is not JsonObject part) continue;
                        var function = part["function"] as JsonObject;
                        toolCalls.Add(new ToolCallPart(
                            Int(part["index"]) ?? i,
                            Str(part["id"]),
                            Str(function?["name"]),
                            Str(function?["arguments"])));
                    }
                }
            }

            finish = Str(choice["finish_reason"]);
        }

        AiUsage? usage = null;
        if (obj["usage"] is JsonObject u)
        {
            usage = new AiUsage(Int(u["prompt_tokens"]), Int(u["completion_tokens"]), Int(u["total_tokens"]));
        }

        return new StreamChunk(content, reasoning, finish, usage, null) { ToolCalls = toolCalls };
    }

    public sealed record StreamChunk(string? Content, string? Reasoning, string? FinishReason, AiUsage? Usage, string? Error)
    {
        public IReadOnlyList<ToolCallPart> ToolCalls { get; init; } = [];
    }

    /// <summary>A piece of one tool call as it streams: whatever of its id, name and arguments this chunk carries.</summary>
    public sealed record ToolCallPart(int Index, string? Id, string? Name, string? Arguments);

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static int? Int(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out int i) ? i : null;

    /// <summary>The provider's own words for a refusal, short and on one line: "model not found" beats "http 404".</summary>
    private static async Task<string> ErrorDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                var node = JsonNode.Parse(text);
                text = node?["detail"]?.ToString() ?? node?["error"]?["message"]?.ToString() ?? node?["error"]?.ToString() ?? node?["message"]?.ToString() ?? text;
            }
            catch (JsonException)
            {
                // Not JSON: an HTML error page, say. Keep the text.
            }

            return OneLine(text);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static string OneLine(string text)
    {
        string flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.StartsWith('<')) return string.Empty; // an HTML page says nothing useful in 160 characters
        return flat.Length <= 160 ? flat : flat[..157] + "...";
    }
}
