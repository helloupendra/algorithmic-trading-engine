using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.Runtime.Signing;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>Signs one request to Bedrock with AWS Signature Version 4: the seam a test replaces.</summary>
public interface IBedrockRequestSigner
{
    /// <summary>
    /// Adds the signature's headers to <paramref name="message"/>, whose body is exactly <paramref name="body"/>. Throws
    /// <see cref="AiAttemptFailedException"/> when it cannot sign (no AWS credentials on the machine).
    /// </summary>
    Task SignAsync(HttpRequestMessage message, byte[] body, string region, CancellationToken cancellationToken) =>
        SignAsync(message, body, region, AwsSigV4BedrockSigner.Service, cancellationToken);

    /// <summary>
    /// The same for another AWS service the desk calls with the same role: Amazon Polly (<c>polly</c>), which speaks
    /// the Telegram answers (<see cref="PollySpeechClient"/>).
    /// </summary>
    Task SignAsync(HttpRequestMessage message, byte[] body, string region, string service, CancellationToken cancellationToken);
}

/// <summary>
/// SigV4 for Bedrock's OpenAI-compatible endpoint (service <c>bedrock-mantle</c>) and for Amazon Polly (<c>polly</c>),
/// through AWSSDK.Core's signer, with the default AWS credential chain: environment, profile, then on the server the EC2
/// instance profile (IMDSv2). The desk passes no key and reads none from its settings or <c>.env</c>.
/// </summary>
/// <remarks>
/// The credentials are looked up on each request, never at start: a desk that never reaches an open model never looks
/// for them. The chain caches what it finds, and the instance profile's credentials refresh themselves before they
/// expire, so each signature is made with the keys of the moment.
/// </remarks>
public sealed class AwsSigV4BedrockSigner : IBedrockRequestSigner
{
    /// <summary>The service name a signature is scoped to.</summary>
    public const string Service = "bedrock-mantle";

    private readonly Func<CancellationToken, Task<AWSCredentials>> _credentials;

    /// <param name="credentials">Where the credentials come from; a test gives fixed fake ones. The default AWS chain otherwise.</param>
    public AwsSigV4BedrockSigner(Func<CancellationToken, Task<AWSCredentials>>? credentials = null)
    {
        _credentials = credentials ?? DefaultChainAsync;
    }

    public Task SignAsync(HttpRequestMessage message, byte[] body, string region, CancellationToken cancellationToken) =>
        SignAsync(message, body, region, Service, cancellationToken);

    public async Task SignAsync(HttpRequestMessage message, byte[] body, string region, string service, CancellationToken cancellationToken)
    {
        try
        {
            var credentials = await _credentials(cancellationToken);
            var request = new AWSSigningRequest
            {
                HttpMethod = message.Method,
                RequestUri = message.RequestUri!,
                Content = body,
            };
            var signed = await AWSSigV4Signer.SignAsync(request, new AWSSigV4Parameters
            {
                Credentials = credentials,
                Region = RegionEndpoint.GetBySystemName(region),
                Service = service,
            }, cancellationToken);

            // Authorization, X-Amz-Date, X-Amz-Content-Sha256 and, for the role's session, X-Amz-Security-Token: the
            // signature covers them all, so every one goes.
            foreach (var (name, value) in signed.Headers)
            {
                message.Headers.Remove(name);
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AiAttemptFailedException)
        {
            // Most often no credentials at all: not the EC2 server, or an instance without its profile.
            string why = string.Join(' ', BedrockRefusals.Mask(ex.Message).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            throw new AiAttemptFailedException($"unreachable (AWS: could not sign the request: {(why.Length <= 160 ? why : why[..157] + "...")})");
        }
    }

    private static Task<AWSCredentials> DefaultChainAsync(CancellationToken cancellationToken) =>
        DefaultAWSCredentialsIdentityResolver.GetCredentialsAsync().WaitAsync(cancellationToken);
}

/// <summary>
/// Open models on Amazon Bedrock (<c>bedrock-oss:</c> ids): Kimi, GLM, DeepSeek, Qwen, gpt-oss, Mistral, Nemotron and
/// MiniMax on Bedrock's OpenAI-compatible endpoint (<c>bedrock-mantle.{region}.api.aws/v1/chat/completions</c>). The third
/// provider beside <see cref="NvidiaChatClient"/> and <see cref="BedrockClaudeClient"/>, with the same request, stream,
/// tool calls and usage as NVIDIA's (<see cref="OpenAiCompatibleChatClient"/>).
/// </summary>
/// <remarks>
/// <para>
/// No key: each request is signed with SigV4 by <see cref="IBedrockRequestSigner"/> just before it is sent, so a
/// rotated instance-role credential is picked up by the next request.
/// </para>
/// <para>
/// Its refusals read as Claude's on Bedrock (<see cref="BedrockRefusals"/>): throttling and a busy model are capacity,
/// so the gateway asks once more before the chain moves on; a model this account may not use says its access is not
/// enabled; a role without the permission says so; an AWS account number in the provider's words is masked.
/// </para>
/// </remarks>
public sealed class BedrockOpenChatClient : OpenAiCompatibleChatClient
{
    public const string HttpClientName = "ai-bedrock-open";

    private readonly IBedrockRequestSigner _signer;

    public BedrockOpenChatClient(IHttpClientFactory httpClientFactory, IOptionsMonitor<AiSettings> settings, IBedrockRequestSigner signer)
        : base(httpClientFactory, settings, HttpClientName)
    {
        _signer = signer;
    }

    public override string Name => AiCatalog.BedrockOpenProviderName;

    public override bool Serves(string model) => AiCatalog.IsBedrockOpen(model);

    public override string? Unavailable(AiSettings settings) =>
        settings.BedrockOpen.Enabled ? null : "Amazon Bedrock's open models are off (Ai:BedrockOpen:Enabled is false)";

    protected override string ChatUrl(AiSettings settings) => AiCatalog.BedrockOpenEndpoint(settings.Bedrock.Region);

    protected override string WireModel(string model) => AiCatalog.BedrockOpenModelId(model);

    protected override Task AuthorizeAsync(HttpRequestMessage message, byte[] body, AiSettings settings, CancellationToken cancellationToken) =>
        _signer.SignAsync(message, body, settings.Bedrock.Region, cancellationToken);

    protected override AiAttemptFailedException Refused(string model, int status, string detail, AiSettings settings) =>
        BedrockRefusals.Describe(WireModel(model), settings.Bedrock.Region, status, detail);

    /// <summary>An error inside the stream: throttling and a busy model are capacity here too.</summary>
    protected override AiAttemptFailedException StreamError(string model, string detail, AiSettings settings)
    {
        bool Says(string words) => detail.Contains(words, StringComparison.OrdinalIgnoreCase);
        if (Says("throttl") || Says("rate limit") || Says("too many requests"))
        {
            return new AiAttemptFailedException($"provider error: throttled by Bedrock{BedrockRefusals.Tail(detail)}", 429);
        }

        return Says("overloaded") || Says("unavailable") || Says("not ready")
            ? new AiAttemptFailedException($"provider error: Bedrock is overloaded{BedrockRefusals.Tail(detail)}", 503)
            : base.StreamError(model, detail, settings);
    }

    protected override string Scrub(string text) => BedrockRefusals.Mask(text);

    /// <summary>The longest a transcription may take: Voxtral Small wrote a voice note down in about 2 s on 11 Oct 2026.</summary>
    public static readonly TimeSpan TranscribeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A voice note written down by an audio model on the same endpoint (Mistral's Voxtral, <see cref="AiVoiceSettings.TranscribeModel"/>):
    /// one request, not streamed, the audio inline as base64 beside the instruction, signed as every request here is.
    /// Throws <see cref="AiAttemptFailedException"/> on any failure, its refusals worded as a chat model's.
    /// </summary>
    /// <param name="modelId">Bedrock's id for the model, such as <c>mistral.voxtral-small-24b-2507</c>.</param>
    /// <param name="format"><c>wav</c> or <c>mp3</c>: the endpoint refused <c>ogg</c> with a 400 (11 Oct 2026).</param>
    public async Task<AiTranscription> TranscribeAsync(
        string modelId, byte[] audio, string format, string instruction, int maxTokens, CancellationToken cancellationToken)
    {
        var s = Settings.CurrentValue;
        byte[] body = Encoding.UTF8.GetBytes(TranscriptionBody(modelId, audio, format, instruction, maxTokens));
        using var message = new HttpRequestMessage(HttpMethod.Post, ChatUrl(s)) { Content = JsonContent(body) };
        using var timer = new CancellationTokenSource(TranscribeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);

        string text;
        try
        {
            await AuthorizeAsync(message, body, s, linked.Token);
            using var response = await Http().SendAsync(message, linked.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw Refused(modelId, (int)response.StatusCode, await ErrorDetailAsync(response, linked.Token), s);
            }

            text = await response.Content.ReadAsStringAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiAttemptFailedException($"timeout: no answer within {TranscribeTimeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException ex)
        {
            throw new AiAttemptFailedException($"unreachable ({ex.HttpRequestError})");
        }

        return ReadTranscription(text);
    }

    /// <summary>The JSON body of a transcription, as it goes over the wire. Public so a test can pin it.</summary>
    public static string TranscriptionBody(string modelId, byte[] audio, string format, string instruction, int maxTokens) => new JsonObject
    {
        ["model"] = modelId,
        ["max_tokens"] = maxTokens,
        ["temperature"] = 0,
        ["messages"] = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "input_audio",
                    ["input_audio"] = new JsonObject { ["data"] = Convert.ToBase64String(audio), ["format"] = format },
                },
                new JsonObject { ["type"] = "text", ["text"] = instruction }),
        }),
    }.ToJsonString();

    /// <summary>The words in <c>choices[0].message.content</c> (a string, or text parts), with the finish and the usage.</summary>
    internal static AiTranscription ReadTranscription(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            throw new AiAttemptFailedException("provider error: the answer was not JSON");
        }

        var choice = root?["choices"] is JsonArray { Count: > 0 } choices ? choices[0] as JsonObject : null;
        var content = choice?["message"]?["content"];
        string words = content is JsonArray parts
            ? string.Concat(parts.OfType<JsonObject>().Select(p => Str(p["text"]) ?? string.Empty))
            : Str(content) ?? string.Empty;
        AiUsage? usage = root?["usage"] is JsonObject u
            ? new AiUsage(Int(u["prompt_tokens"]), Int(u["completion_tokens"]), Int(u["total_tokens"]))
            : null;
        if (string.IsNullOrWhiteSpace(words)) throw new AiAttemptFailedException("empty answer");
        return new AiTranscription(words.Trim(), Str(choice?["finish_reason"]) ?? string.Empty, usage);
    }
}

/// <summary>What an audio model wrote down, how it stopped, and the tokens it used.</summary>
public sealed record AiTranscription(string Text, string FinishReason, AiUsage? Usage);
