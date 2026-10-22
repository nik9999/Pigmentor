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
    /// The fused RRF score decides the primary order. Equal fused scores tie-break
    /// deterministically on the maximum original leg score, followed by chunk ID
    /// (<see cref="StringComparer.Ordinal"/>).
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

        var fused = new Dictionary<string, (DocumentChunk Chunk, double FusedScore, double MaxOriginalScore, double? DenseScore, double? SparseScore, ReadOnlyMemory<float>? Vector)>(StringComparer.Ordinal);

        foreach (var list in lists)
        {
            if (list is null)
            {
                continue;
            }

            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                var key = item.Chunk.ChunkId;
                var contribution = 1.0 / (K + i + 1);
                if (fused.TryGetValue(key, out var existing))
                {
                    var dense = existing.DenseScore;
                    if (item.DenseScore.HasValue)
                    {
                        dense = existing.DenseScore.HasValue
                            ? Math.Max(existing.DenseScore.Value, item.DenseScore.Value)
                            : item.DenseScore;
                    }

                    var sparse = existing.SparseScore;
                    if (item.SparseScore.HasValue)
                    {
                        sparse = existing.SparseScore.HasValue
                            ? Math.Max(existing.SparseScore.Value, item.SparseScore.Value)
                            : item.SparseScore;
                    }

                    var maxOrig = Math.Max(existing.MaxOriginalScore, item.Score);
                    var vector = existing.Vector ?? item.Vector;

                    fused[key] = (existing.Chunk, existing.FusedScore + contribution, maxOrig, dense, sparse, vector);
                }
                else
                {
                    fused[key] = (item.Chunk, contribution, item.Score, item.DenseScore, item.SparseScore, item.Vector);
                }
            }
        }

        return [.. fused.Values
            .OrderByDescending(x => x.FusedScore)
            .ThenByDescending(x => x.MaxOriginalScore)
            .ThenBy(x => x.Chunk.ChunkId, StringComparer.Ordinal)
            .Take(topK)
            .Select(x => new RetrievalResult(x.Chunk, x.FusedScore)
            {
                FusedScore = x.FusedScore,
                DenseScore = x.DenseScore,
                SparseScore = x.SparseScore,
                Vector = x.Vector
            })];
    }
}
