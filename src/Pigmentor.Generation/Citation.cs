using Pigmentor.Core.Documents;

namespace Pigmentor.Generation;

/// <summary>
/// A citation linking a generated answer to a specific retrieved source chunk.
/// </summary>
/// <param name="SourceIndex">1-based reference index matching [S1], [S2] in context and generated text.</param>
/// <param name="DocumentId">Stable parent document identifier.</param>
/// <param name="ChunkId">Stable chunk identifier.</param>
/// <param name="Title">Human-readable document title if available.</param>
/// <param name="Snippet">Text excerpt from the source chunk.</param>
/// <param name="Metadata">Parent document metadata for provenance and domain categorization.</param>
/// <param name="Label">Citation tag label, e.g. "S1". Defaults to S{SourceIndex}.</param>
public sealed record Citation(
    int SourceIndex,
    string DocumentId,
    string ChunkId,
    string? Title,
    string? Snippet,
    DocumentMetadata? Metadata = null,
    string? Label = null)
{
    /// <summary>
    /// Explicit citation tag label matching [S1], [S2] in generated text.
    /// </summary>
    public string Label { get; init; } = Label ?? $"S{SourceIndex}";

    /// <summary>
    /// Creates a citation from a 1-based source index and a <see cref="RetrievalResult"/>.
    /// </summary>
    public static Citation FromRetrievalResult(int sourceIndex, RetrievalResult result, string? label = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(result.Chunk);

        return new Citation(
            SourceIndex: sourceIndex,
            DocumentId: result.Chunk.DocumentId,
            ChunkId: result.Chunk.ChunkId,
            Title: result.Chunk.Metadata?.Title,
            Snippet: result.Chunk.Text,
            Metadata: result.Chunk.Metadata,
            Label: label ?? $"S{sourceIndex}");
    }
}
