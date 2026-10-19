using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval;

/// <summary>
/// Pass-through no-op reranker that preserves the existing candidate order and takes top-K.
/// </summary>
public sealed class NoOpReranker : IReranker
{
    /// <summary>Shared singleton instance.</summary>
    public static NoOpReranker Instance { get; } = new();

    public Task<IReadOnlyList<RetrievalResult>> RerankAsync(
        string query,
        IReadOnlyList<RetrievalResult> candidates,
        int topK,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        cancellationToken.ThrowIfCancellationRequested();

        if (topK <= 0 || candidates.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<RetrievalResult>>([]);
        }

        IReadOnlyList<RetrievalResult> result = candidates.Count <= topK
            ? candidates
            : candidates.Take(topK).ToList();

        return Task.FromResult(result);
    }
}

