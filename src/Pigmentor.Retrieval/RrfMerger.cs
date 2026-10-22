using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval;

/// <summary>
/// Reciprocal Rank Fusion (RRF) — Cormack et al. 2009. Combines two or
/// more ranked result lists into a single ranking by summing
/// <c>1 / (k + rank)</c> across the input lists. Robust to score-scale
/// mismatch between retrievers (the headline reason it beats weighted
/// blending in practice).
/// </summary>
public sealed class RrfMerger
{
    public int K { get; }

    public RrfMerger(int k = 60)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);
        K = k;
    }

    /// <summary>Fuse two result lists by reciprocal rank.</summary>
    /// <remarks>
    /// Only the fused RRF score decides the order — the inputs' original
    /// similarity scores are not carried into the comparison, so equal fused
    /// scores fall back to encounter order (first list first). An earlier
    /// version of this comment promised a tie-break on the original score,
    /// which the implementation has never done.
    /// </remarks>
    public IReadOnlyList<RetrievalResult> Merge(
        IReadOnlyList<RetrievalResult> a,
        IReadOnlyList<RetrievalResult> b,
        int topK)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Merge([a, b], topK);
    }

    /// <summary>Fuse two or more result lists by reciprocal rank.</summary>
    public IReadOnlyList<RetrievalResult> Merge(
        IEnumerable<IReadOnlyList<RetrievalResult>> lists,
        int topK)
    {
        ArgumentNullException.ThrowIfNull(lists);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);

        var fused = new Dictionary<string, (DocumentChunk Chunk, double Score)>(StringComparer.Ordinal);

        foreach (var list in lists)
        {
            if (list is null)
            {
                continue;
            }

            for (int i = 0; i < list.Count; i++)
            {
                var key = list[i].Chunk.ChunkId;
                var contribution = 1.0 / (K + i + 1);
                if (fused.TryGetValue(key, out var existing))
                {
                    fused[key] = (existing.Chunk, existing.Score + contribution);
                }
                else
                {
                    fused[key] = (list[i].Chunk, contribution);
                }
            }
        }

        return [.. fused.Values
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => new RetrievalResult(x.Chunk, x.Score))];
    }
}
