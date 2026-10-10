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
public sealed record AiMessage(string Role, string Content, IReadOnlyList<AiToolCall>? ToolCalls = null, string? ToolCallId = null)
{
    /// <summary>
    /// An assistant turn exactly as the model wrote it, thinking included, so the same model reads it back unchanged
    /// in the next round of a tool loop (<see cref="BedrockClaudeClient"/>); null for a provider that needs nothing.
    /// </summary>
    public AiReplay? Replay { get; init; }
}

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
    bool ToolsClosed = false)
{
    /// <summary>The tier the call walks (judge, analyst, extract); null for a model's health test. Sets Claude's effort.</summary>
    public string? Tier { get; init; }

    /// <summary>
    /// How much of <see cref="SystemPrompt"/>, from the start, is the same on every call of its agent: the agent's own
    /// prompt, before the memories and the time. A provider that caches prompts caches up to here; 0 for all of it.
    /// </summary>
    public int StableSystemChars { get; init; }
}

/// <summary>Tokens a call used, as the provider counted them.</summary>
/// <param name="PromptTokens">Every token read, cached or not.</param>
public sealed record AiUsage(int? PromptTokens, int? CompletionTokens, int? TotalTokens)
{
    /// <summary>Of <see cref="PromptTokens"/>, those read from the provider's prompt cache (Claude); null when it does not say.</summary>
    public int? CacheReadTokens { get; init; }

    /// <summary>Of <see cref="PromptTokens"/>, those written to the prompt cache.</summary>
    public int? CacheWriteTokens { get; init; }
}

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

    /// <summary>The answer's blocks in order, thinking included, for the same model to read back (<see cref="AiMessage.Replay"/>).</summary>
    public IReadOnlyList<AiReplayBlock>? Replay { get; init; }
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
/// endpoint, always streamed (<see cref="OpenAiCompatibleChatClient"/>), the
/// embeddings and the model list.
/// </summary>
/// <remarks>
/// The key goes in the Authorization header and nowhere else: no outcome,
/// log line or exception carries it.
/// </remarks>
public sealed class NvidiaChatClient : OpenAiCompatibleChatClient
{
    public const string HttpClientName = "ai-provider";

    private readonly IOptionsMonitor<AiSettings> _settings;

    public NvidiaChatClient(IHttpClientFactory httpClientFactory, IOptionsMonitor<AiSettings> settings)
        : base(httpClientFactory, settings, HttpClientName)
    {
        _settings = settings;
    }

    public string ChatEndpoint => ChatUrl(_settings.CurrentValue);

    public override string Name => AiCatalog.ProviderName;

    /// <summary>Every model a chain names that is not on Bedrock.</summary>
    public override bool Serves(string model) => !AiCatalog.OnBedrock(model);

    public override string? Unavailable(AiSettings settings) => settings.KeyConfigured ? null : "no NVIDIA_API_KEY on the server";

    protected override string ChatUrl(AiSettings settings) => $"{Base(settings)}/chat/completions";

    protected override Task AuthorizeAsync(HttpRequestMessage message, byte[] body, AiSettings settings, CancellationToken cancellationToken)
    {
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        return Task.CompletedTask;
    }

    private string BaseUrl => Base(_settings.CurrentValue);

    private static string Base(AiSettings settings) => (settings.BaseUrl is { Length: > 0 } b ? b : AiCatalog.DefaultBaseUrl).TrimEnd('/');

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
            using var response = await Http().SendAsync(message, timeout.Token);
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
            using var response = await Http().SendAsync(message, timeout.Token);
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
}
