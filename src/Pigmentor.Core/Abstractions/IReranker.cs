using Pigmentor.Core.Documents;

namespace Pigmentor.Core.Abstractions;

/// <summary>
/// Re-scores and reorders a pool of candidate <see cref="RetrievalResult"/>s for a given query.
/// </summary>
public interface IReranker
{
    /// <summary>
    /// Re-scores and ranks the candidate retrieval results.
    /// </summary>
    /// <param name="query">The user query.</param>
    /// <param name="candidates">The unranked or broadly retrieved candidate pool.</param>
    /// <param name="topK">The maximum number of reranked candidates to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A ranked list of up to <paramref name="topK"/> results.</returns>
    Task<IReadOnlyList<RetrievalResult>> RerankAsync(
        string query,
        IReadOnlyList<RetrievalResult> candidates,
        int topK,
        CancellationToken cancellationToken = default);
}

