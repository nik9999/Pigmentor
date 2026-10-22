using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;

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
    private readonly HybridRetrievalOptions _options;
    private readonly RrfMerger _merger;

    /// <summary>Configured hybrid retrieval options.</summary>
    public HybridRetrievalOptions Options => _options;

    public HybridRetriever(
        IRetriever dense,
        IRetriever sparse,
        HybridRetrievalOptions? options = null,
        RrfMerger? merger = null,
        double? denseMinScore = null,
        double? sparseMinScore = null)
    {
        ArgumentNullException.ThrowIfNull(dense);
        ArgumentNullException.ThrowIfNull(sparse);
        _dense = (denseMinScore.HasValue && denseMinScore.Value > 0.0)
            ? new MinScoreFilter(dense, denseMinScore.Value)
            : dense;
        _sparse = (sparseMinScore.HasValue && sparseMinScore.Value > 0.0)
            ? new MinScoreFilter(sparse, sparseMinScore.Value)
            : sparse;
        _options = options ?? new HybridRetrievalOptions();
        _merger = merger ?? new RrfMerger(_options.RrfK);
    }

    public HybridRetriever(
        IRetriever dense,
        IRetriever sparse,
        RrfMerger? merger,
        double? denseMinScore = null,
        double? sparseMinScore = null)
        : this(dense, sparse, options: null, merger, denseMinScore, sparseMinScore)
    {
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

        // Candidate pool per leg (default 50; scales up if request.TopK > pool size)
        var candidateK = Math.Max(_options.CandidatePoolPerLeg, request.TopK);
        var effectiveFilter = ReferenceEquals(request.Filter, MetadataFilter.All)
            ? null
            : (request.Filter ?? QueryScopeResolver.ResolveScope(request.Query));
        var legRequest = request with { TopK = candidateK, Filter = effectiveFilter };

        using var legCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.LegTimeout > TimeSpan.Zero && _options.LegTimeout != Timeout.InfiniteTimeSpan)
        {
            legCts.CancelAfter(_options.LegTimeout);
        }
        var legToken = legCts.Token;

        var denseTask = _dense.RetrieveAsync(legRequest, legToken);
        var sparseTask = _sparse.RetrieveAsync(legRequest, legToken);

        try
        {
            await Task.WhenAll(denseTask, sparseTask).WaitAsync(legToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && legCts.IsCancellationRequested)
        {
            // Leg timeout occurred. If one leg completed successfully, salvage its results.
            if (denseTask.IsCompletedSuccessfully && !sparseTask.IsCompletedSuccessfully)
            {
                return denseTask.Result.Take(request.TopK).ToList();
            }
            if (sparseTask.IsCompletedSuccessfully && !denseTask.IsCompletedSuccessfully)
            {
                return sparseTask.Result.Take(request.TopK).ToList();
            }
            throw new TimeoutException($"Hybrid retrieval legs timed out after {_options.LegTimeout.TotalSeconds:F1} seconds.");
        }

        return _merger.Merge(
            denseTask.Result,
            _options.DenseWeight,
            sparseTask.Result,
            _options.SparseWeight,
            request.TopK);
    }
}
