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

    public HybridDatabaseRetriever(IRetriever vector, IRetriever graph, FusionService? fusion = null)
    {
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentNullException.ThrowIfNull(graph);
        _vector = vector;
        _graph = graph;
        _fusion = fusion ?? new FusionService();
    }

    public string Strategy => $"hybrid-db({_vector.Strategy}+{_graph.Strategy}, {_fusion.Strategy})";

    public async Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);

        var candidateK = Math.Max(topK * 2, topK + 5);
        var vectorTask = _vector.RetrieveAsync(query, candidateK, cancellationToken);
        var graphTask = _graph.RetrieveAsync(query, candidateK, cancellationToken);
        await Task.WhenAll(vectorTask, graphTask).ConfigureAwait(false);

        return _fusion.Fuse(vectorTask.Result, graphTask.Result, topK);
    }
}
