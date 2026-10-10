namespace AlgoTrading.Infrastructure.Ai;

/// <summary>
/// A provider of chat models: NVIDIA's NIM API (<see cref="NvidiaChatClient"/>) or Claude on Amazon Bedrock
/// (<see cref="BedrockClaudeClient"/>). The gateway asks whichever serves the model a chain names, so one chain can
/// fall back from one provider to the other.
/// </summary>
public interface IAiChatProvider
{
    /// <summary>As the console names it.</summary>
    string Name { get; }

    /// <summary>Whether <paramref name="model"/>, as a chain names it, is this provider's.</summary>
    bool Serves(string model);

    /// <summary>Why this provider cannot be asked now (switched off, no key), in a few words; null when it can.</summary>
    string? Unavailable(AiSettings settings);

    /// <summary>
    /// Streams one model's answer into <paramref name="onPiece"/>, and says how it ended. Throws
    /// <see cref="AiAttemptFailedException"/> when the model fails in any way the next model might not; only the
    /// caller's own cancellation escapes as <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<AiAttemptEnd> StreamAsync(string model, AiChatRequest request, Func<AiStreamPiece, ValueTask> onPiece, CancellationToken cancellationToken);
}

/// <summary>Picks the provider for a model a chain names: <c>bedrock:</c> ids go to Bedrock, the rest to NVIDIA.</summary>
public sealed class AiChatRouter
{
    private readonly IReadOnlyList<IAiChatProvider> _providers;

    public AiChatRouter(NvidiaChatClient nvidia, BedrockClaudeClient bedrock) : this([bedrock, nvidia]) { }

    /// <summary>The providers in the order they are asked who serves a model; the first that does wins.</summary>
    public AiChatRouter(IReadOnlyList<IAiChatProvider> providers) => _providers = providers;

    public IAiChatProvider? For(string model) => _providers.FirstOrDefault(p => p.Serves(model));

    /// <summary>Why <paramref name="model"/> cannot be asked now, or null when it can.</summary>
    public string? Unavailable(string model, AiSettings settings) =>
        For(model) is { } provider ? provider.Unavailable(settings) : "no provider serves it";
}

/// <summary>An assistant turn as one model wrote it, for that model alone to read back.</summary>
/// <param name="Model">The chain's id of the model that wrote it.</param>
public sealed record AiReplay(string Model, IReadOnlyList<AiReplayBlock> Blocks);

/// <summary>One block of an assistant turn, in the order the model wrote it.</summary>
public abstract record AiReplayBlock
{
    /// <summary>Its reasoning, with the signature that lets the same model trust it again (the text may be a summary, or empty).</summary>
    public sealed record Thinking(string Reasoning, string Signature) : AiReplayBlock;

    /// <summary>Reasoning the provider encrypted: passed back as it came.</summary>
    public sealed record RedactedThinking(string Data) : AiReplayBlock;

    public sealed record Text(string Value) : AiReplayBlock;

    /// <summary>A tool it asked for, with its arguments as the JSON text it wrote.</summary>
    public sealed record ToolUse(string Id, string Name, string Arguments) : AiReplayBlock;
}
