using System.Text.RegularExpressions;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>
/// What a refusal from Bedrock means, for both of its chat clients (<see cref="BedrockClaudeClient"/>,
/// <see cref="BedrockOpenChatClient"/>), so the attempts table reads the same whichever was asked.
/// </summary>
internal static class BedrockRefusals
{
    private static readonly Regex AccountNumber = new(@"\b\d{12}\b", RegexOptions.CultureInvariant);

    /// <summary>The text with every AWS account number masked: Bedrock's refusals quote the role's ARN.</summary>
    public static string Mask(string text) => AccountNumber.Replace(text, "[account]");

    /// <summary>
    /// An HTTP refusal in the attempts table's words. Throttling, a busy model and a model not ready yet are capacity
    /// (429/503), so the gateway asks once more; a model whose access is not enabled, an instance role without the
    /// permission and credentials Bedrock refused each say so.
    /// </summary>
    /// <param name="modelId">Bedrock's id for the model, without the chain's prefix.</param>
    /// <param name="detail">The provider's words, on one line, already masked.</param>
    public static AiAttemptFailedException Describe(string modelId, string region, int status, string detail)
    {
        bool Says(string words) => detail.Contains(words, StringComparison.OrdinalIgnoreCase);

        bool credentials = Says("security token") || Says("signature") || Says("credential");
        if (status is 401 or 403 && credentials)
        {
            return new AiAttemptFailedException($"http {status}: Bedrock refused the server's AWS credentials{Tail(detail)}", status);
        }

        // IAM's own words when the instance role has no policy that allows the call.
        if (status is 401 or 403 && Says("not authorized to perform"))
        {
            return new AiAttemptFailedException(
                $"http {status}: the server's AWS role is not allowed to call Bedrock (an IAM permission is missing){Tail(detail)}", status);
        }

        if (status is 403 or 404
            || status == 401 && (Says("not available") || Says("access"))
            || status == 400 && Says("access") && Says("model"))
        {
            return new AiAttemptFailedException(
                $"http {status}: Bedrock model access is not enabled for {modelId} in {region}{Tail(detail)}", status);
        }

        if (status == 401) return new AiAttemptFailedException($"http 401: Bedrock refused the server's AWS credentials{Tail(detail)}", 401);

        bool notReady = Says("ModelNotReady") || Says("not ready");
        bool throttled = status == 429 || Says("throttl");
        if (throttled) return new AiAttemptFailedException($"http {status}: throttled by Bedrock{Tail(detail)}", 429);
        if (notReady) return new AiAttemptFailedException($"http {status}: model not ready on Bedrock, temporarily{Tail(detail)}", 503);
        if (status is 502 or 503 or 529 || Says("ServiceUnavailable"))
        {
            return new AiAttemptFailedException($"http {status}: Bedrock is overloaded{Tail(detail)}", status == 529 ? 503 : status);
        }

        return new AiAttemptFailedException(detail.Length > 0 ? $"http {status}: {detail}" : $"http {status}", status);
    }

    public static string Tail(string detail) => detail.Length > 0 ? $": {detail}" : string.Empty;
}
