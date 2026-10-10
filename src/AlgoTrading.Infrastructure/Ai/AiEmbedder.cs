using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>
/// The desk's embeddings, for the docs search and the agents' memory: which model makes the vectors now, and the
/// call to it, whichever provider serves it.
/// </summary>
/// <remarks>
/// <para>
/// Vectors from two models cannot be compared (Nemotron Embed's and Titan's are not even the same length), so one
/// model is active at a time and every reader uses only that model's vectors. The active model is NVIDIA's
/// (<see cref="AiCatalog.EmbeddingModel"/>) until the re-embedding (<see cref="AiReembedder"/>) has made every
/// memory's and every doc passage's vector with Bedrock's Titan; only then does it store Titan as active
/// (<see cref="ActiveModelKey"/>). Until then, and whenever Bedrock is off again, search and memory work as before.
/// </para>
/// <para>
/// A vector whose model is not the active one is made again when it is next needed: the doc indexer re-embeds such
/// passages on its next run, and a memory is re-embedded when it is next ranked.
/// </para>
/// </remarks>
public sealed class AiEmbedder(NvidiaChatClient nvidia, IOptionsMonitor<AiSettings> settings, BedrockEmbeddingClient? bedrock = null)
{
    /// <summary>The <c>system_settings</c> row naming the active embedding model, written by the re-embedding alone.</summary>
    public const string ActiveModelKey = "ai.embedding.model";

    /// <summary>The model the re-embedding switches to: Bedrock's Titan, as a chain names it.</summary>
    public static string Target(AiSettings s) => AiCatalog.BedrockPrefix + s.Bedrock.EmbeddingModel;

    /// <summary>The active model: the stored one while its provider can be asked, else NVIDIA's (the behaviour before Bedrock).</summary>
    public static string Resolve(string? stored, AiSettings s) =>
        stored is { Length: > 0 } model && Available(model, s) ? model : AiCatalog.EmbeddingModel;

    private static bool Available(string model, AiSettings s) => AiCatalog.IsBedrock(model) ? s.Bedrock.Enabled : s.KeyConfigured;

    public async Task<string> ActiveModelAsync(TradingDbContext db, CancellationToken cancellationToken)
    {
        string? stored = await db.SystemSettings.AsNoTracking()
            .Where(x => x.Key == ActiveModelKey)
            .Select(x => x.Value)
            .FirstOrDefaultAsync(cancellationToken);
        return Resolve(stored, settings.CurrentValue);
    }

    /// <summary>Whether <paramref name="model"/> can be asked now: NVIDIA's with its key, Bedrock's while Bedrock is on.</summary>
    public bool CanEmbed(string model) =>
        Available(model, settings.CurrentValue) && (!AiCatalog.IsBedrock(model) || bedrock is not null);

    /// <summary>
    /// Vectors for <paramref name="inputs"/>, in order. <paramref name="inputType"/> (<c>passage</c> or <c>query</c>)
    /// matters to Nemotron Embed; Titan embeds both alike. Throws <see cref="AiAttemptFailedException"/> on any failure.
    /// </summary>
    public Task<(IReadOnlyList<float[]> Vectors, int? Tokens)> EmbedAsync(
        string model, IReadOnlyList<string> inputs, string inputType, CancellationToken cancellationToken)
    {
        if (!AiCatalog.IsBedrock(model)) return nvidia.EmbedAsync(model, inputs, inputType, cancellationToken);
        if (bedrock is null || !settings.CurrentValue.Bedrock.Enabled)
        {
            throw new AiAttemptFailedException("Amazon Bedrock is off (Ai:Bedrock:Enabled is false)");
        }

        return bedrock.EmbedAsync(AiCatalog.BedrockModelId(model), inputs, cancellationToken);
    }
}
