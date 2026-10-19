using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval;

/// <summary>
/// "Lost in the middle" mitigation (Liu et al., 2023). LLMs attend most
/// reliably to context at the START and END of a long prompt and tend to
/// overlook material buried in the MIDDLE. <see cref="Interleave"/> reorders a
/// relevance-ranked result list so the strongest chunks sit at the two ends and
/// the weakest land in the middle, where the model's attention is weakest.
///
/// <para>
/// Pure and deterministic: no LLM call, no I/O. In the pipeline this composes
/// AFTER reranking (Ch 9 has produced the relevance order) and BEFORE
/// augmentation (Ch 10 lays the chunks into the prompt) — it is a final
/// presentation-order pass over an already-ranked list, not a re-ranker.
/// </para>
/// </summary>
public static class ContextOrdering
{
    /// <summary>
    /// Reorder a relevance-ranked list so the best results bracket the prompt.
    /// Walks the input best-first, placing items alternately at the FRONT (in
    /// order) and at the BACK, so rank-1 lands at index 0 and rank-2 at the last
    /// index. For example <c>[1, 2, 3, 4, 5]</c> becomes <c>[1, 3, 5, 4, 2]</c>.
    /// </summary>
    /// <param name="rankedByRelevance">
    /// Results in descending relevance order (rank-1 first). May be empty.
    /// </param>
    /// <returns>
    /// A new list of the same items reordered for end-weighting. An empty input
    /// yields an empty list; the item count is always preserved.
    /// </returns>
    public static IReadOnlyList<RetrievalResult> Interleave(
        IReadOnlyList<RetrievalResult> rankedByRelevance)
    {
        ArgumentNullException.ThrowIfNull(rankedByRelevance);

        var n = rankedByRelevance.Count;
        if (n == 0)
        {
            return [];
        }

        var result = new RetrievalResult[n];
        var front = 0;
        var back = n - 1;
        for (var i = 0; i < n; i++)
        {
            // Even ranks (0,2,4,…) go to the front; odd ranks (1,3,5,…) to the back.
            if ((i & 1) == 0)
            {
                result[front++] = rankedByRelevance[i];
            }
            else
            {
                result[back--] = rankedByRelevance[i];
            }
        }

        return result;
    }
}
