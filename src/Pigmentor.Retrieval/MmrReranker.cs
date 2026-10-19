using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval;

/// <summary>
/// Reranker that applies Maximal Marginal Relevance (MMR) diversity selection
/// to balance relevance against redundancy among candidate chunks.
/// </summary>
public sealed class MmrReranker : IReranker
{
    private readonly IEmbeddingService _embeddingService;
    private readonly double _lambda;

    public MmrReranker(IEmbeddingService embeddingService, double lambda = 0.5)
    {
        _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
        if (lambda is < 0.0 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(lambda), lambda, "lambda must be in the closed interval [0, 1].");
        }

        _lambda = lambda;
    }

    public async Task<IReadOnlyList<RetrievalResult>> RerankAsync(
        string query,
        IReadOnlyList<RetrievalResult> candidates,
        int topK,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (topK <= 0 || candidates.Count == 0)
        {
            return [];
        }

        if (candidates.Count == 1)
        {
            return candidates;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var mmrCandidates = new List<MmrCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vector = await _embeddingService.EmbedQueryAsync(candidate.Chunk.Text, cancellationToken).ConfigureAwait(false);
            mmrCandidates.Add(new MmrCandidate(candidate.Chunk, vector, candidate.Score));
        }

        return MmrSelector.Select(mmrCandidates, topK, _lambda);
    }
}

