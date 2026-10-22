namespace Pigmentor.Core.Documents;

/// <summary>
/// A single hit returned by an <see cref="Abstractions.IRetriever"/>.
/// </summary>
/// <param name="Chunk">The retrieved chunk.</param>
/// <param name="Score">
/// Retriever-specific relevance score. Higher means more relevant.
/// For dense retrievers this is typically cosine similarity in [-1, 1] but
/// hybrid retrievers and rerankers may produce values outside that range.
/// Consumers should inspect <see cref="DenseScore"/> or <see cref="SparseScore"/>
/// for leg-specific calibrated similarity metrics.
/// </param>
public sealed record RetrievalResult(DocumentChunk Chunk, double Score)
{
    /// <summary>Fused (RRF) rank score. Used for ordering.</summary>
    public double FusedScore { get; init; } = Score;

    /// <summary>Best original similarity across legs (cosine for dense).</summary>
    public double? DenseScore { get; init; }

    /// <summary>Best original sparse similarity (e.g. BM25 score).</summary>
    public double? SparseScore { get; init; }

    /// <summary>The dense embedding vector of the chunk, if carried from vector storage.</summary>
    public ReadOnlyMemory<float>? Vector { get; init; }

    /// <summary>Cross-encoder reranker score (e.g. calibrated sigmoid relevance probability or logit).</summary>
    public double? RerankerScore { get; init; }
}
