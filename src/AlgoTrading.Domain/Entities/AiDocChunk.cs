namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One passage of the desk's written docs (a module doc, a strategy spec)
/// with its embedding, for the Desk Assistant's <c>search_docs</c> tool.
/// </summary>
/// <remarks>
/// Rebuilt from the files: a passage whose text changed gets a new vector, and
/// passages of a deleted file go. The vector is kept as a float array; the
/// corpus is small (the repo's <c>docs/</c>), so search is a cosine over all of
/// them in memory rather than a vector index.
/// </remarks>
public class AiDocChunk
{
    public long Id { get; set; }

    /// <summary>The file, relative to <c>docs/</c>, e.g. <c>modules/ai.md</c>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Where in the file: its title and section, e.g. "AI workspace › Desk tools".</summary>
    public string Heading { get; set; } = string.Empty;

    /// <summary>Its place in the file, from 0.</summary>
    public int Ordinal { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>SHA-256 of <see cref="Heading"/> and <see cref="Text"/>: an unchanged passage is not embedded again.</summary>
    public string Hash { get; set; } = string.Empty;

    public float[] Embedding { get; set; } = [];

    /// <summary>The embedding model; a passage embedded by another model is embedded again.</summary>
    public string Model { get; set; } = string.Empty;

    public DateTime IndexedUtc { get; set; }
}
