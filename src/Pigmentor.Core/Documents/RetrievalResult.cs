namespace Pigmentor.Core.Documents;

/// <summary>
/// A single hit returned by an <see cref="Abstractions.IRetriever"/>.
/// </summary>
/// <param name="Chunk">The retrieved chunk.</param>
/// <param name="Score">
/// Retriever-specific relevance score. Higher means more relevant.
/// For dense retrievers this is typically cosine similarity in [-1, 1] but
/// hybrid retrievers (Ch 8) and rerankers (Ch 9) may produce values outside
/// that range — consumers should treat the score as a comparable scalar
/// within a single result list, not as an absolute calibration.
/// </param>
public sealed record RetrievalResult(DocumentChunk Chunk, double Score);
