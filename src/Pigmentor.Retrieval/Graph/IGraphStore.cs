namespace Pigmentor.Retrieval.Graph;

/// <summary>
/// Pigmentor domain port for any property-graph store. Adapters: Neo4j
/// (ships in Ch 13 below), Memgraph + Cosmos DB Gremlin live as Phase-7
/// homework.
/// </summary>
public interface IGraphStore
{
    Task EnsureSchemaExistsAsync(CancellationToken cancellationToken = default);
    Task UpsertEntityAsync(GraphEntity entity, CancellationToken cancellationToken = default);
    Task UpsertRelationAsync(GraphRelation relation, CancellationToken cancellationToken = default);

    /// <summary>Run an arbitrary Cypher (or equivalent) read query.</summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object>>> QueryAsync(
        string query,
        IReadOnlyDictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Find all entities within <paramref name="maxHops"/> of any node whose
    /// name fuzzy-matches one of <paramref name="entityNames"/>. Used by
    /// <see cref="GraphRetriever"/> to assemble a contextual subgraph.
    /// </summary>
    Task<IReadOnlyList<GraphEntity>> TraverseAsync(
        IEnumerable<string> entityNames,
        int maxHops,
        CancellationToken cancellationToken = default);
}
