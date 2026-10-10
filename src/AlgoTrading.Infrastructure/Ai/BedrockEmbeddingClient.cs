using System.Text;
using System.Text.Json.Nodes;
using Amazon;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>One <c>bedrock-runtime</c> InvokeModel call, JSON in and JSON out: the seam a test replaces.</summary>
public interface IBedrockRuntimeInvoker
{
    /// <summary>The model's answer body. Throws <see cref="AiAttemptFailedException"/> on any failure.</summary>
    Task<string> InvokeAsync(string region, string modelId, string body, CancellationToken cancellationToken);
}

/// <summary>
/// The AWS SDK's Bedrock runtime client, made on the first call for a region. Its credentials are the default AWS
/// chain's (the EC2 instance profile on the server); the desk passes none.
/// </summary>
public sealed class AwsBedrockRuntimeInvoker : IBedrockRuntimeInvoker, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AmazonBedrockRuntimeClient> _clients = new(StringComparer.Ordinal);

    public async Task<string> InvokeAsync(string region, string modelId, string body, CancellationToken cancellationToken)
    {
        AmazonBedrockRuntimeClient client;
        lock (_gate)
        {
            if (!_clients.TryGetValue(region, out client!))
            {
                _clients[region] = client = new AmazonBedrockRuntimeClient(RegionEndpoint.GetBySystemName(region));
            }
        }

        try
        {
            using var request = new MemoryStream(Encoding.UTF8.GetBytes(body));
            var response = await client.InvokeModelAsync(new InvokeModelRequest
            {
                ModelId = modelId,
                ContentType = "application/json",
                Accept = "application/json",
                Body = request,
            }, cancellationToken);
            using var reader = new StreamReader(response.Body, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException)
        {
            throw Failed(ex, modelId, region);
        }
    }

    /// <summary>
    /// What an AWS failure means, for the call's row: throttling and a busy or not-ready model are capacity (429/503);
    /// a model whose access is not enabled says so. Public so a test can pin each case.
    /// </summary>
    public static AiAttemptFailedException Failed(Exception ex, string modelId, string region) => ex switch
    {
        ThrottlingException or ServiceQuotaExceededException => new AiAttemptFailedException("http 429: throttled by Bedrock", 429),
        ModelNotReadyException => new AiAttemptFailedException("http 503: model not ready on Bedrock, temporarily", 503),
        ServiceUnavailableException => new AiAttemptFailedException("http 503: Bedrock is overloaded", 503),
        AccessDeniedException or ResourceNotFoundException =>
            new AiAttemptFailedException($"http 403: Bedrock model access is not enabled for {modelId} in {region}", 403),
        ModelTimeoutException => new AiAttemptFailedException("timeout: Bedrock's model did not answer in time"),
        ValidationException v => new AiAttemptFailedException($"http 400: {OneLine(v.Message)}", 400),
        AmazonServiceException s => new AiAttemptFailedException($"http {(int)s.StatusCode}: {OneLine(s.Message)}", (int)s.StatusCode),
        // A client-side failure: most often no AWS credentials on the machine (no instance profile).
        _ => new AiAttemptFailedException($"unreachable (AWS: {OneLine(ex.Message)})"),
    };

    private static string OneLine(string text)
    {
        string flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        flat = System.Text.RegularExpressions.Regex.Replace(flat, @"\b\d{12}\b", "[account]");
        return flat.Length <= 200 ? flat : flat[..197] + "...";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var client in _clients.Values) client.Dispose();
            _clients.Clear();
        }
    }
}

/// <summary>
/// Embeddings from Amazon Titan Text Embeddings V2 on Bedrock (<c>amazon.titan-embed-text-v2:0</c>): one text per
/// call, so a batch goes a few calls at a time (<see cref="AiBedrockSettings.EmbeddingConcurrency"/>), in order.
/// </summary>
public sealed class BedrockEmbeddingClient(IOptionsMonitor<AiSettings> settings, IBedrockRuntimeInvoker invoker)
{
    /// <summary>Titan V2 takes at most 50,000 characters (and 8,192 tokens).</summary>
    public const int MaxChars = 50_000;

    public async Task<(IReadOnlyList<float[]> Vectors, int? Tokens)> EmbedAsync(
        string modelId, IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        var b = settings.CurrentValue.Bedrock;
        var vectors = new float[inputs.Count][];
        int tokens = 0;
        using var gate = new SemaphoreSlim(Math.Max(1, b.EmbeddingConcurrency));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        async Task One(int i)
        {
            await gate.WaitAsync(stop.Token);
            try
            {
                stop.Token.ThrowIfCancellationRequested();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                string text = string.IsNullOrWhiteSpace(inputs[i]) ? "(empty)" : inputs[i].Length > MaxChars ? inputs[i][..MaxChars] : inputs[i];
                string body = new JsonObject { ["inputText"] = text, ["dimensions"] = b.EmbeddingDimensions, ["normalize"] = true }.ToJsonString();
                string answer;
                try
                {
                    answer = await invoker.InvokeAsync(b.Region, modelId, body, timeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !stop.IsCancellationRequested)
                {
                    throw new AiAttemptFailedException("timeout");
                }

                var (vector, used) = Parse(answer);
                vectors[i] = vector;
                Interlocked.Add(ref tokens, used ?? 0);
            }
            catch (AiAttemptFailedException)
            {
                // One failure ends the batch: the rest are not worth paying for.
                stop.Cancel();
                throw;
            }
            finally
            {
                gate.Release();
            }
        }

        var all = Task.WhenAll(Enumerable.Range(0, inputs.Count).Select(One));
        try
        {
            await all;
        }
        catch (Exception) when (all.Exception?.InnerExceptions.OfType<AiAttemptFailedException>().FirstOrDefault() is { } first)
        {
            throw first;
        }

        return (vectors, tokens);
    }

    /// <summary>Titan's answer: <c>embedding</c> and <c>inputTextTokenCount</c>. Public for tests.</summary>
    public static (float[] Vector, int? Tokens) Parse(string answer)
    {
        try
        {
            var root = JsonNode.Parse(answer);
            var vector = (root?["embedding"] as JsonArray ?? []).Select(x => x!.GetValue<float>()).ToArray();
            if (vector.Length == 0) throw new AiAttemptFailedException("no vector in the answer");
            return (vector, root?["inputTextTokenCount"] is JsonValue t && t.TryGetValue(out int n) ? n : null);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            throw new AiAttemptFailedException("unreadable answer");
        }
    }
}
