using Pigmentor.Core.Documents;
using Pigmentor.Core.Numerics;

namespace Pigmentor.Retrieval;

/// <summary>
/// Greedy MMR-like near-duplicate chunk suppression based on dense embedding cosine similarity.
/// Suppresses near-identical fragments from distinct documents (e.g. operational manual vs product passport
/// for the same technical equipment) to maximize novelty and context diversity within the LLM context budget.
/// </summary>
public static class NearDuplicateDeduplicator
{
    /// <summary>
    /// Default cosine similarity threshold above which chunks from distinct documents are considered near-duplicates.
    /// </summary>
    public const double DefaultSimilarityCeiling = 0.94;

    /// <summary>
    /// Sequentially scans already-ranked candidates and suppresses candidates whose cosine similarity
    /// to any already-admitted higher-ranking candidate meets or exceeds <paramref name="similarityCeiling"/>.
    /// </summary>
    /// <param name="ranked">Already-ranked candidates ordered by descending relevance.</param>
    /// <param name="similarityCeiling">Cosine similarity threshold in (0, 1]. Default is 0.94.</param>
    /// <param name="crossDocumentOnly">
    /// When true (default), similarity suppression is only applied between chunks belonging to different documents,
    /// preserving adjacent or overlapping chunks within the same document.
    /// When false, near-duplicates are suppressed globally across all documents.
    /// </param>
    /// <returns>Deduplicated list preserving the original relative ranking of admitted candidates.</returns>
    public static IReadOnlyList<RetrievalResult> DeduplicateNearDuplicates(
        IReadOnlyList<RetrievalResult> ranked,
        double similarityCeiling = DefaultSimilarityCeiling,
        bool crossDocumentOnly = true)
    {
        ArgumentNullException.ThrowIfNull(ranked);

        if (similarityCeiling is <= 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(similarityCeiling),
                similarityCeiling,
                "similarityCeiling must be greater than 0.0 and less than or equal to 1.0.");
        }

        if (ranked.Count <= 1)
        {
            return ranked;
        }

        var kept = new List<RetrievalResult>(ranked.Count);
        var seenChunkIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var r in ranked)
        {
            // 1. Exact ChunkId deduplication: identical chunk is never admitted twice
            if (!seenChunkIds.Add(r.Chunk.ChunkId))
            {
                continue;
            }

            // 2. Near-duplicate suppression against already-admitted higher-ranking candidates
            var isDuplicate = false;
            for (var i = 0; i < kept.Count; i++)
            {
                var k = kept[i];

                // If crossDocumentOnly, chunks within the same document are preserved
                if (crossDocumentOnly && string.Equals(k.Chunk.DocumentId, r.Chunk.DocumentId, StringComparison.Ordinal))
                {
                    continue;
                }

                // Check exact text equality (handles missing vectors or identical textual content)
                if (k.Chunk.Text.AsSpan().Trim().Equals(r.Chunk.Text.AsSpan().Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    isDuplicate = true;
                    break;
                }

                // Check dense embedding cosine similarity if both candidates carry vectors
                if (k.Vector.HasValue && r.Vector.HasValue &&
                    k.Vector.Value.Length > 0 && k.Vector.Value.Length == r.Vector.Value.Length)
                {
                    var sim = CosineKernel.Cosine(k.Vector.Value.Span, r.Vector.Value.Span);
                    if (sim >= similarityCeiling)
                    {
                        isDuplicate = true;
                        break;
                    }
                }
            }

            if (!isDuplicate)
            {
                kept.Add(r);
            }
        }

        return kept;
    }
}
