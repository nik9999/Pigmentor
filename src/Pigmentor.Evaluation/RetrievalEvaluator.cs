using Pigmentor.Core.Documents;
using Pigmentor.Evaluation.Cases;

namespace Pigmentor.Evaluation;

/// <summary>One labeled QA pair from the golden eval set.</summary>
public sealed record GoldenItem(
    string Query,
    IReadOnlySet<string> ExpectedDocumentIds,
    string? ReferenceAnswer = null,
    IReadOnlySet<string>? ExpectedChunkIds = null);

/// <summary>Aggregated retrieval metrics for a single eval run.</summary>
public sealed record RetrievalMetrics(
    double RecallAtK,
    double PrecisionAtK,
    double Mrr,
    double NdcgAtK,
    int K,
    int QueryCount,
    double HitRateAtK = 0.0);

/// <summary>
/// Computes deterministic retrieval metrics — HitRate@K, Recall@K, Precision@K, MRR,
/// nDCG@K — over a list of evaluated samples.
/// </summary>
public static class RetrievalEvaluator
{
    public static RetrievalMetrics Evaluate(
        IReadOnlyList<(GoldenItem Gold, IReadOnlyList<RetrievalResult> Hits)> samples,
        int k)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);
        if (samples.Count == 0)
        {
            return new RetrievalMetrics(0, 0, 0, 0, k, 0, 0);
        }

        double hitRate = 0, recall = 0, precision = 0, mrr = 0, ndcg = 0;
        var evaluatedSamples = 0;
        foreach (var (gold, hits) in samples)
        {
            var topK = hits.Take(k).ToList();
            var expectedIds = gold.ExpectedChunkIds is { Count: > 0 }
                ? gold.ExpectedChunkIds
                : gold.ExpectedDocumentIds;
            if (expectedIds.Count == 0)
            {
                continue;
            }

            evaluatedSamples++;
            var retrievedIds = gold.ExpectedChunkIds is { Count: > 0 }
                ? topK.Select(h => h.Chunk.ChunkId).ToList()
                : topK.Select(h => h.Chunk.DocumentId).ToList();
            var hitSet = new HashSet<string>(retrievedIds, StringComparer.Ordinal);
            int relevantInTopK = retrievedIds.Count(expectedIds.Contains);

            bool hasHit = retrievedIds.Any(expectedIds.Contains);

            if (hasHit)
            {
                hitRate += 1.0;
            }

            recall += (double)hitSet.Intersect(expectedIds).Count() / expectedIds.Count;
            precision += (double)relevantInTopK / k;

            // MRR = 1/rank of first hit.
            for (int i = 0; i < retrievedIds.Count; i++)
            {
                if (expectedIds.Contains(retrievedIds[i]))
                {
                    mrr += 1.0 / (i + 1);
                    break;
                }
            }

            // nDCG@K with binary relevance.
            double dcg = 0, idcg = 0;
            var seenRelevantIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < retrievedIds.Count; i++)
            {
                if (expectedIds.Contains(retrievedIds[i]) && seenRelevantIds.Add(retrievedIds[i]))
                {
                    dcg += 1.0 / Math.Log2(i + 2);
                }
            }
            for (int i = 0; i < Math.Min(k, expectedIds.Count); i++)
            {
                idcg += 1.0 / Math.Log2(i + 2);
            }
            ndcg += idcg == 0 ? 0 : Math.Clamp(dcg / idcg, 0.0, 1.0);
        }

        var n = evaluatedSamples;
        if (n == 0)
        {
            return new RetrievalMetrics(0, 0, 0, 0, k, 0, 0);
        }

        return new RetrievalMetrics(
            RecallAtK: recall / n,
            PrecisionAtK: precision / n,
            Mrr: mrr / n,
            NdcgAtK: ndcg / n,
            K: k,
            QueryCount: n,
            HitRateAtK: hitRate / n);
    }

    public static RetrievalMetrics Evaluate(
        IReadOnlyList<(RagEvaluationCase Case, IReadOnlyList<RetrievalResult> Hits)> samples,
        int k)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var mapped = samples
            .Select(s => (
                Gold: new GoldenItem(
                    s.Case.Question,
                    new HashSet<string>(s.Case.RelevantDocumentIds, StringComparer.Ordinal),
                    ExpectedChunkIds: s.Case.RelevantChunkIds is { Count: > 0 }
                        ? new HashSet<string>(s.Case.RelevantChunkIds, StringComparer.Ordinal)
                        : null),
                s.Hits))
            .ToList();

        return Evaluate(mapped, k);
    }
}
