using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>
/// A provider whose models answer on an OpenAI-compatible <c>/chat/completions</c>, always streamed: NVIDIA's NIM API
/// (<see cref="NvidiaChatClient"/>) and the open models on Amazon Bedrock (<see cref="BedrockOpenChatClient"/>). The
/// request, the stream, the tool calls and the usage are read here once; a provider says only where it is, how a
/// request is authorized, and what its refusals mean.
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
/// Each request is authorized afresh (<see cref="AuthorizeAsync"/>), within the
/// first-token limit: NVIDIA's key, or a SigV4 signature over the body with the
/// credentials of the moment.
/// </para>
/// </remarks>
public abstract class OpenAiCompatibleChatClient : IAiChatProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<AiSettings> _settings;
    private readonly string _httpClientName;

    protected OpenAiCompatibleChatClient(IHttpClientFactory httpClientFactory, IOptionsMonitor<AiSettings> settings, string httpClientName)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _httpClientName = httpClientName;
    }

    /// <summary>The settings it reads, for a helper built on it without its own (<see cref="AiEmbedder"/>).</summary>
    internal IOptionsMonitor<AiSettings> Settings => _settings;

    public abstract string Name { get; }

    public abstract bool Serves(string model);

    public abstract string? Unavailable(AiSettings settings);

    /// <summary>The chat endpoint the settings point at.</summary>
    protected abstract string ChatUrl(AiSettings settings);

    /// <summary>The model's id as the endpoint knows it: the chain's id, less any provider prefix.</summary>
    protected virtual string WireModel(string model) => model;

    /// <summary>
    /// Authorizes one request whose body is <paramref name="body"/>, just before it is sent. Throws
    /// <see cref="AiAttemptFailedException"/> when it cannot (no credentials).
    /// </summary>
    protected abstract Task AuthorizeAsync(HttpRequestMessage message, byte[] body, AiSettings settings, CancellationToken cancellationToken);

    /// <summary>What a refusal (an HTTP status that is not success) means, for the attempts table.</summary>
    /// <param name="detail">The provider's own words, on one line; empty when it said nothing useful.</param>
    protected virtual AiAttemptFailedException Refused(string model, int status, string detail, AiSettings settings) =>
        new(detail.Length > 0 ? $"http {status}: {detail}" : $"http {status}", status);

    /// <summary>What an error sent in the stream, after the headers said success, means.</summary>
    protected virtual AiAttemptFailedException StreamError(string model, string detail, AiSettings settings) =>
        new($"provider error: {detail}");

    /// <summary>The provider's words as they may be kept: a provider masks what must not be stored. Applied before they are cut short.</summary>
    protected virtual string Scrub(string text) => text;

    protected HttpClient Http() => _httpClientFactory.CreateClient(_httpClientName);

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

        // The exact bytes sent are the ones a signature covers.
        byte[] body = Encoding.UTF8.GetBytes(RequestBody(WireModel(model), request));
        using var message = new HttpRequestMessage(HttpMethod.Post, ChatUrl(s)) { Content = JsonContent(body) };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var http = Http();
        HttpResponseMessage response;
        Arm();
        try
        {
            await AuthorizeAsync(message, body, s, linked.Token);
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
                throw Refused(model, (int)response.StatusCode, await ErrorDetailAsync(response, linked.Token), s);
            }

            string finish = string.Empty;
            AiUsage? usage = null;
            bool done = false;
            bool answered = false;
            var calls = new SortedDictionary<int, (string? Id, StringBuilder Name, StringBuilder Args)>();
            var markup = new ToolMarkupFilter();

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
                using var reader = new StreamReader(stream, Encoding.UTF8);
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
                    if (chunk.Error is not null) throw StreamError(model, OneLine(Scrub(chunk.Error)), s);
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
                        string shown = markup.Feed(chunk.Content);
                        answered |= !string.IsNullOrWhiteSpace(shown);
                        if (shown.Length > 0) await Deliver(onPiece, new AiStreamPiece.Content(shown));
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

            // A held-back "<" that never became tool markup is the answer's own text.
            string held = markup.Flush();
            if (held.Length > 0)
            {
                answered |= !string.IsNullOrWhiteSpace(held);
                await Deliver(onPiece, new AiStreamPiece.Content(held));
            }

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

    /// <summary>A JSON body, UTF-8, as these bytes and nothing else.</summary>
    protected static ByteArrayContent JsonContent(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    /// <summary>The JSON body sent for one model, by the id its endpoint knows. Public so a test can pin what goes over the wire.</summary>
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

    /// <summary>
    /// One <c>data:</c> line of the stream, read leniently: a field that is missing or of another type is skipped. An
    /// error comes back as the provider wrote it, for the provider to scrub before it is cut to one line.
    /// </summary>
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
            return new StreamChunk(null, null, null, null, text);
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

    protected static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    protected static int? Int(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out int i) ? i : null;

    /// <summary>The provider's own words for a refusal, scrubbed, short and on one line: "model not found" beats "http 404".</summary>
    protected async Task<string> ErrorDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                var node = JsonNode.Parse(text);
                text = node?["detail"]?.ToString() ?? node?["error"]?["message"]?.ToString() ?? node?["error"]?.ToString()
                    ?? node?["message"]?.ToString() ?? node?["Message"]?.ToString() ?? text;
            }
            catch (JsonException)
            {
                // Not JSON: an HTML error page, say. Keep the text.
            }

            return OneLine(Scrub(text));
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
        {
            return string.Empty;
        }
    }

    protected static string OneLine(string text)
    {
        string flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.StartsWith('<')) return string.Empty; // an HTML page says nothing useful in 160 characters
        return flat.Length <= 160 ? flat : flat[..157] + "...";
    }
}
