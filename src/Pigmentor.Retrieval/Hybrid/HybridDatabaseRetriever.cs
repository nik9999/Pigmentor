using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval.Hybrid;

/// <summary>
/// Vector + graph hybrid retriever. Runs the dense vector path and the
/// graph traversal path in parallel with <c>Task.WhenAll</c>, then
/// fuses the two ranked lists via a configurable <see cref="FusionService"/>.
/// </summary>
public sealed class HybridDatabaseRetriever : IRetriever
{
    private readonly IRetriever _vector;
    private readonly IRetriever _graph;
    private readonly FusionService _fusion;
    private readonly int _candidatePoolPerLeg;

    public HybridDatabaseRetriever(
        IRetriever vector,
        IRetriever graph,
        FusionService? fusion = null,
        int candidatePoolPerLeg = 50)
    {
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentNullException.ThrowIfNull(graph);
        _vector = vector;
        _graph = graph;
        _fusion = fusion ?? new FusionService();
        _candidatePoolPerLeg = candidatePoolPerLeg > 0 ? candidatePoolPerLeg : 50;
    }

    public string Strategy => $"hybrid-db({_vector.Strategy}+{_graph.Strategy}, {_fusion.Strategy})";

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

        var candidateK = Math.Max(_candidatePoolPerLeg, request.TopK);
        var legRequest = request with { TopK = candidateK };
        var vectorTask = _vector.RetrieveAsync(legRequest, cancellationToken);
        var graphTask = _graph.RetrieveAsync(legRequest, cancellationToken);
        await Task.WhenAll(vectorTask, graphTask).ConfigureAwait(false);

        return _fusion.Fuse(vectorTask.Result, graphTask.Result, request.TopK);
    }
}
