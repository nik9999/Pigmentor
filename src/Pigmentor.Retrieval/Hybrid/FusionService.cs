using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval.Hybrid;

/// <summary>The available fusion strategies for combining multiple retriever results.</summary>
public enum FusionStrategy
{
    /// <summary>Reciprocal Rank Fusion — score-scale agnostic, the production default.</summary>
    Rrf,
    /// <summary>Linear weighted blend of normalized scores. Use when the score scales are calibrated.</summary>
    Weighted,
    /// <summary>Try the first list; only fall through to the second if the first is below <c>FloorCount</c>.</summary>
    Cascade,
}

/// <summary>Configurable fusion of two ranked result lists into a single ranking.</summary>
public sealed class FusionService
{
    public FusionStrategy Strategy { get; }
    public double WeightA { get; }
    public double WeightB { get; }
    public int CascadeFloorCount { get; }
    public int RrfK { get; }

    public FusionService(
        FusionStrategy strategy = FusionStrategy.Rrf,
        double weightA = 0.5,
        double weightB = 0.5,
        int cascadeFloorCount = 3,
        int rrfK = 60)
    {
        Strategy = strategy;
        WeightA = weightA;
        WeightB = weightB;
        CascadeFloorCount = cascadeFloorCount;
        RrfK = rrfK;
    }

    public IReadOnlyList<RetrievalResult> Fuse(
        IReadOnlyList<RetrievalResult> a,
        IReadOnlyList<RetrievalResult> b,
        int topK) => Strategy switch
        {
            FusionStrategy.Rrf => Rrf(a, b, topK),
            FusionStrategy.Weighted => Weighted(a, b, topK),
            FusionStrategy.Cascade => Cascade(a, b, topK),
            _ => throw new InvalidOperationException($"Unsupported fusion strategy {Strategy}"),
        };

    private IReadOnlyList<RetrievalResult> Rrf(
        IReadOnlyList<RetrievalResult> a,
        IReadOnlyList<RetrievalResult> b,
        int topK) => new RrfMerger(RrfK).Merge(a, b, topK);

    private IReadOnlyList<RetrievalResult> Weighted(
        IReadOnlyList<RetrievalResult> a,
        IReadOnlyList<RetrievalResult> b,
        int topK)
    {
        var normA = NormalizeScores(a);
        var normB = NormalizeScores(b);
        var fused = new Dictionary<string, (DocumentChunk Chunk, double Score)>(StringComparer.Ordinal);
        foreach (var (chunk, score) in normA)
        {
            fused[chunk.ChunkId] = (chunk, WeightA * score);
        }
        foreach (var (chunk, score) in normB)
        {
            if (fused.TryGetValue(chunk.ChunkId, out var existing))
            {
                fused[chunk.ChunkId] = (existing.Chunk, existing.Score + WeightB * score);
            }
            else
            {
                fused[chunk.ChunkId] = (chunk, WeightB * score);
            }
        }
        return [.. fused.Values
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => new RetrievalResult(x.Chunk, x.Score))];
    }

    private IReadOnlyList<RetrievalResult> Cascade(
        IReadOnlyList<RetrievalResult> a,
        IReadOnlyList<RetrievalResult> b,
        int topK) => a.Count >= CascadeFloorCount
            ? [.. a.Take(topK)]
            : Rrf(a, b, topK);

    private static IEnumerable<(DocumentChunk Chunk, double Score)> NormalizeScores(IReadOnlyList<RetrievalResult> results)
    {
        if (results.Count == 0)
        {
            yield break;
        }
        var max = results.Max(r => r.Score);
        var min = results.Min(r => r.Score);
        var range = Math.Max(max - min, 1e-9);
        foreach (var r in results)
        {
            yield return (r.Chunk, (r.Score - min) / range);
        }
    }
}
