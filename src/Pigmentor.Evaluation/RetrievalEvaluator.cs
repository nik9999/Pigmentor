using Pigmentor.Core.Documents;

namespace Pigmentor.Evaluation;

/// <summary>One labeled QA pair from the golden eval set.</summary>
public sealed record GoldenItem(
    string Query,
    IReadOnlySet<string> ExpectedDocumentIds,
    string? ReferenceAnswer = null);

/// <summary>Aggregated retrieval metrics for a single eval run.</summary>
public sealed record RetrievalMetrics(
    double RecallAtK,
    double PrecisionAtK,
    double Mrr,
    double NdcgAtK,
    int K,
    int QueryCount);

/// <summary>
/// Computes RAGAS-style retrieval metrics — Recall@K, Precision@K, MRR,
/// nDCG@K — over a list of (query, expected document ids, retriever
/// results) triples. Used by `tools/eval-runner` and the GitHub Actions
/// faithfulness gate.
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
            return new RetrievalMetrics(0, 0, 0, 0, k, 0);
        }

        double recall = 0, precision = 0, mrr = 0, ndcg = 0;
        foreach (var (gold, hits) in samples)
        {
            var topK = hits.Take(k).ToList();
            var retrievedDocIds = topK.Select(h => h.Chunk.DocumentId).ToList();
            var hitSet = new HashSet<string>(retrievedDocIds, StringComparer.Ordinal);
            int relevantInTopK = retrievedDocIds.Count(id => gold.ExpectedDocumentIds.Contains(id));

            recall += gold.ExpectedDocumentIds.Count == 0 ? 0
                : (double)hitSet.Intersect(gold.ExpectedDocumentIds).Count() / gold.ExpectedDocumentIds.Count;
            precision += (double)relevantInTopK / k;

            // MRR = 1/rank of first hit.
            for (int i = 0; i < retrievedDocIds.Count; i++)
            {
                if (gold.ExpectedDocumentIds.Contains(retrievedDocIds[i]))
                {
                    mrr += 1.0 / (i + 1);
                    break;
                }
            }

            // nDCG@K with binary relevance.
            double dcg = 0, idcg = 0;
            for (int i = 0; i < retrievedDocIds.Count; i++)
            {
                if (gold.ExpectedDocumentIds.Contains(retrievedDocIds[i]))
                {
                    dcg += 1.0 / Math.Log2(i + 2);
                }
            }
            for (int i = 0; i < Math.Min(k, gold.ExpectedDocumentIds.Count); i++)
            {
                idcg += 1.0 / Math.Log2(i + 2);
            }
            ndcg += idcg == 0 ? 0 : dcg / idcg;
        }

        var n = samples.Count;
        return new RetrievalMetrics(
            RecallAtK: recall / n,
            PrecisionAtK: precision / n,
            Mrr: mrr / n,
            NdcgAtK: ndcg / n,
            K: k,
            QueryCount: n);
    }
}
