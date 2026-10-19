namespace Pigmentor.Core.Documents;

/// <summary>
/// A <see cref="DocumentChunk"/> paired with its dense embedding vector,
/// ready to be persisted into a vector store.
/// </summary>
/// <param name="Chunk">The textual chunk this embedding was produced from.</param>
/// <param name="Vector">
/// The dense embedding. The dimensionality depends on the embedding model
/// (e.g. 1536 for <c>text-embedding-3-small</c>, 768 for <c>nomic-embed-text</c>).
/// Stored as <see cref="ReadOnlyMemory{T}"/> so callers can slice without
/// copying when working with <see cref="Span{T}"/>-based pipelines (Ch 21).
/// </param>
/// <param name="EmbeddingModel">
/// The model name that produced <see cref="Vector"/>. Recorded so that
/// retrieval-time queries can be embedded with the matching model and
/// drift detection (Ch 22) can identify cohorts.
/// </param>
public sealed record EmbeddedChunk(
    DocumentChunk Chunk,
    ReadOnlyMemory<float> Vector,
    string EmbeddingModel);
