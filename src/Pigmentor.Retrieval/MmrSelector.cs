using Pigmentor.Core.Documents;
using Pigmentor.Core.Numerics;

namespace Pigmentor.Retrieval;

/// <summary>
/// A retrieval candidate carrying the data Maximal Marginal Relevance needs:
/// the chunk, its dense embedding vector, and the retriever's relevance score.
/// <see cref="RetrievalResult"/> deliberately carries only <c>Chunk</c> + <c>Score</c>
/// (no vector), so MMR cannot operate on it directly — diversity selection needs
/// the vectors to measure candidate-to-candidate similarity. Build these tuples
/// from the embedded candidates (re-embed, or carry vectors through retrieval).
/// </summary>
/// <param name="Chunk">The candidate chunk.</param>
/// <param name="Vector">The chunk's dense embedding (the same space the relevance score was computed in).</param>
/// <param name="Relevance">The retriever's relevance score for this candidate (higher is better).</param>
public readonly record struct MmrCandidate(
    DocumentChunk Chunk,
    ReadOnlyMemory<float> Vector,
    double Relevance,
    RetrievalResult? OriginalResult = null);

/// <summary>
/// Maximal Marginal Relevance (Carbonell &amp; Goldstein, 1998) diversity
/// selection. Greedily picks the candidate that maximizes
/// <c>λ·rel(d) − (1−λ)·max_{s∈selected} cosine(d, s)</c>, trading raw relevance
/// (λ→1) against novelty versus already-selected results (λ→0). The classic fix
/// for a top-K full of near-duplicate chunks of the same passage.
///
/// <para>
/// Pure and deterministic: no I/O, no model calls. Wiring MMR into the live
/// pipeline is a reranking-stage (Ch 9) concern, or needs a vector-carrying
/// retrieval path — this selector is the building block either way.
/// </para>
/// </summary>
public static class MmrSelector
{
    /// <summary>
    /// Select up to <paramref name="k"/> candidates in MMR order.
    /// </summary>
    /// <param name="candidates">The candidate pool. Order is irrelevant; the algorithm re-ranks.</param>
    /// <param name="k">Maximum number of results to return. The output may be shorter if the pool is smaller.</param>
    /// <param name="lambda">
    /// Relevance-vs-diversity knob in <c>[0, 1]</c>. <c>1.0</c> reduces to pure
    /// relevance order; <c>0.0</c> is pure diversity; <c>0.5</c>–<c>0.7</c> are
    /// common in practice.
    /// </param>
    /// <returns>The MMR-ordered selection as <see cref="RetrievalResult"/>s, scored by their original relevance.</returns>
    public static IReadOnlyList<RetrievalResult> Select(
        IReadOnlyList<MmrCandidate> candidates,
        int k,
        double lambda = 0.5)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);
        if (lambda is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lambda), lambda, "lambda must be in the closed interval [0, 1].");
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        var remaining = new List<MmrCandidate>(candidates);
        var selected = new List<MmrCandidate>(Math.Min(k, remaining.Count));
        var take = Math.Min(k, remaining.Count);

        while (selected.Count < take)
        {
            var bestIndex = -1;
            var bestScore = double.NegativeInfinity;

            for (var i = 0; i < remaining.Count; i++)
            {
                var candidate = remaining[i];

                // Penalty = similarity to the most-similar already-selected result.
                double maxSimToSelected = 0.0;
                for (var s = 0; s < selected.Count; s++)
                {
                    var sim = CosineKernel.Cosine(candidate.Vector.Span, selected[s].Vector.Span);
                    if (sim > maxSimToSelected)
                    {
                        maxSimToSelected = sim;
                    }
                }

                var mmr = (lambda * candidate.Relevance) - ((1.0 - lambda) * maxSimToSelected);
                if (mmr > bestScore)
                {
                    bestScore = mmr;
                    bestIndex = i;
                }
            }

            selected.Add(remaining[bestIndex]);
            remaining.RemoveAt(bestIndex);
        }

        return [.. selected.Select(c =>
        {
            if (c.OriginalResult != null)
            {
                return c.OriginalResult with { Score = c.Relevance };
            }
            return new RetrievalResult(c.Chunk, c.Relevance) { Vector = c.Vector };
        })];
    }
}
