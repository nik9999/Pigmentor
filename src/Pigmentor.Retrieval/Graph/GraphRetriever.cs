using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval.Graph;

/// <summary>
/// Retriever that pulls a contextual subgraph for the query and returns
/// each entity as a synthetic <see cref="DocumentChunk"/>. The query's
/// own entities are extracted via <see cref="EntityExtractor"/>; the
/// graph store traverses up to <see cref="MaxHops"/> from each match.
/// </summary>
public sealed class GraphRetriever : IRetriever
{
    private readonly EntityExtractor _extractor;
    private readonly IGraphStore _graph;
    public int MaxHops { get; }

    public GraphRetriever(EntityExtractor extractor, IGraphStore graph, int maxHops = 2)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHops);
        _extractor = extractor;
        _graph = graph;
        MaxHops = maxHops;
    }

    public string Strategy => "graph";

    public async Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);

        var extraction = await _extractor.ExtractAsync(query, cancellationToken).ConfigureAwait(false);
        var names = extraction.Entities.Select(e => e.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0)
        {
            return Array.Empty<RetrievalResult>();
        }

        var entities = await _graph.TraverseAsync(names, MaxHops, cancellationToken).ConfigureAwait(false);
        var meta = new DocumentMetadata("graph", "graph", "Graph", "All",
            "Internal", "GraphNode", 2026, "graph",
            new DateOnly(2026, 1, 1), "Knowledge graph subgraph");

        return [.. entities
            .Take(topK)
            .Select((e, i) =>
            {
                var text = $"{e.Type}: {e.Name}" +
                    (e.Properties.Count == 0 ? "" : " (" + string.Join("; ",
                        e.Properties.Select(p => $"{p.Key}={p.Value}")) + ")");
                var chunk = new DocumentChunk($"graph#{e.Id}", "graph", i, text, 0, text.Length, meta);
                return new RetrievalResult(chunk, 1.0 / (i + 1));
            })];
    }
}
