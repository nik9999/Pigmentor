using Neo4j.Driver;

namespace Pigmentor.Retrieval.Graph;

/// <summary>
/// Neo4j adapter for <see cref="IGraphStore"/> using the official driver.
/// Targets the Neo4j 5.26 (community, LTS) server — installed natively or via
/// AuraDB (see docs/local-setup.md) — with the 6.x SemVer driver.
/// All read/write work runs inside managed transactions
/// (<see cref="IAsyncSession.ExecuteReadAsync{T}(System.Func{IAsyncQueryRunner, Task{T}}, System.Action{TransactionConfigBuilder})"/>
/// / <c>ExecuteWriteAsync</c>) so the driver applies its built-in retry on
/// transient failures and leader switches.
/// </summary>
public sealed class Neo4jGraphStore : IGraphStore, IAsyncDisposable
{
    private readonly IDriver _driver;

    public Neo4jGraphStore(IDriver driver)
    {
        ArgumentNullException.ThrowIfNull(driver);
        _driver = driver;
    }

    public async Task EnsureSchemaExistsAsync(CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();
        await session.ExecuteWriteAsync(async tx =>
        {
            var c1 = await tx.RunAsync(
                "CREATE CONSTRAINT entity_id IF NOT EXISTS FOR (e:Entity) REQUIRE e.id IS UNIQUE").ConfigureAwait(false);
            await c1.ConsumeAsync().ConfigureAwait(false);

            // Full-text index over Entity.name — backs the seed lookup in
            // TraverseAsync so it no longer scans every :Entity node.
            var c2 = await tx.RunAsync(
                "CREATE FULLTEXT INDEX entity_name IF NOT EXISTS FOR (e:Entity) ON EACH [e.name]").ConfigureAwait(false);
            await c2.ConsumeAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async Task UpsertEntityAsync(GraphEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await using var session = _driver.AsyncSession();
        var props = new Dictionary<string, object>(entity.Properties.ToDictionary(p => p.Key, p => (object)p.Value))
        {
            ["id"] = entity.Id,
            ["name"] = entity.Name,
            ["type"] = entity.Type,
        };
        await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MERGE (e:Entity { id: $id }) SET e += $props, e:`" + Sanitize(entity.Type) + "`",
                new { id = entity.Id, props }).ConfigureAwait(false);
            await cursor.ConsumeAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async Task UpsertRelationAsync(GraphRelation relation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relation);
        await using var session = _driver.AsyncSession();
        var rel = Sanitize(relation.Type);
        await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                $"MATCH (a:Entity {{ id: $from }}), (b:Entity {{ id: $to }}) " +
                $"MERGE (a)-[r:`{rel}`]->(b) SET r += $props",
                new { from = relation.FromId, to = relation.ToId, props = relation.Properties }).ConfigureAwait(false);
            await cursor.ConsumeAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object>>> QueryAsync(
        string query,
        IReadOnlyDictionary<string, object>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        await using var session = _driver.AsyncSession();
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(query, parameters as object ?? new { }).ConfigureAwait(false);
            var results = new List<IReadOnlyDictionary<string, object>>();
            await foreach (var record in cursor.ConfigureAwait(false))
            {
                results.Add(record.Values.ToDictionary(kv => kv.Key, kv => kv.Value));
            }
            return (IReadOnlyList<IReadOnlyDictionary<string, object>>)results;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Seeds the traversal via the <c>entity_name</c> full-text index
    /// (created in <see cref="EnsureSchemaExistsAsync"/>) instead of a
    /// label-wide scan, then expands up to <paramref name="maxHops"/> hops.
    /// </summary>
    public async Task<IReadOnlyList<GraphEntity>> TraverseAsync(
        IEnumerable<string> entityNames,
        int maxHops,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityNames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHops);
        var names = entityNames.ToList();
        if (names.Count == 0)
        {
            return Array.Empty<GraphEntity>();
        }

        var cypher =
            $"UNWIND $names AS name " +
            $"CALL db.index.fulltext.queryNodes('entity_name', name) YIELD node AS start " +
            $"OPTIONAL MATCH (start)-[*1..{maxHops}]-(neighbour:Entity) " +
            $"WITH collect(DISTINCT start) + collect(DISTINCT neighbour) AS nodes " +
            $"UNWIND nodes AS n RETURN DISTINCT n";

        var rows = await QueryAsync(cypher, new Dictionary<string, object> { ["names"] = names }, cancellationToken).ConfigureAwait(false);
        var entities = new List<GraphEntity>();
        foreach (var row in rows)
        {
            if (row["n"] is not INode node)
            {
                continue;
            }
            var props = node.Properties.Where(p => p.Key is not ("id" or "name" or "type"))
                .ToDictionary(p => p.Key, p => p.Value?.ToString() ?? string.Empty);
            entities.Add(new GraphEntity(
                Id: node.Properties.GetValueOrDefault("id")?.ToString() ?? "",
                Type: node.Properties.GetValueOrDefault("type")?.ToString() ?? "",
                Name: node.Properties.GetValueOrDefault("name")?.ToString() ?? "",
                Properties: props));
        }
        return entities;
    }

    public ValueTask DisposeAsync() => _driver.DisposeAsync();

    /// <summary>Allow only [A-Za-z0-9_] in label / relation names — the rest of Cypher is parameterized.</summary>
    private static string Sanitize(string name)
    {
        var span = name.AsSpan();
        Span<char> buf = stackalloc char[span.Length];
        int j = 0;
        for (int i = 0; i < span.Length; i++)
        {
            if (char.IsLetterOrDigit(span[i]) || span[i] == '_')
            {
                buf[j++] = span[i];
            }
        }
        return j == 0 ? "Unknown" : new string(buf[..j]);
    }
}
