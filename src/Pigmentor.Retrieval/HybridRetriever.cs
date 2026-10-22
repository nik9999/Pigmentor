using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval;

/// <summary>
/// Hybrid retriever — fans out to a dense and a sparse retriever in
/// parallel, then fuses the two ranked lists with <see cref="RrfMerger"/>.
/// Composition over inheritance: any pair of <see cref="IRetriever"/>s
/// can be hybridized this way.
/// </summary>
public sealed class HybridRetriever : IRetriever
{
    private readonly IRetriever _dense;
    private readonly IRetriever _sparse;
    private readonly RrfMerger _merger;

    public HybridRetriever(IRetriever dense, IRetriever sparse, RrfMerger? merger = null)
    {
        ArgumentNullException.ThrowIfNull(dense);
        ArgumentNullException.ThrowIfNull(sparse);
        _dense = dense;
        _sparse = sparse;
        _merger = merger ?? new RrfMerger();
    }

    public string Strategy => $"hybrid({_dense.Strategy}+{_sparse.Strategy})";

    public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default) =>
        RetrieveAsync(new RetrievalRequest(query, topK), cancellationToken);

    public async Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.TopK);

        // Fan out 2× topK candidates per leg; the merger picks the final topK.
        var candidateK = Math.Max(request.TopK * 2, request.TopK + 5);
        var effectiveFilter = request.Filter ?? QueryScopeResolver.ResolveScope(request.Query);
        var legRequest = request with { TopK = candidateK, Filter = effectiveFilter };
        var denseTask = _dense.RetrieveAsync(legRequest, cancellationToken);
        var sparseTask = _sparse.RetrieveAsync(legRequest, cancellationToken);

        // WaitAsync ensures the await unblocks promptly on cancellation even if
        // one inner retriever has a bug that doesn't cooperate with the token.
        await Task.WhenAll(denseTask, sparseTask).WaitAsync(cancellationToken).ConfigureAwait(false);

        return _merger.Merge(denseTask.Result, sparseTask.Result, request.TopK);
    }
}
