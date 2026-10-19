using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval.Graph;

/// <summary>
/// Document → graph ingestion pipeline. For each chunk: extract entities
/// + relations via <see cref="EntityExtractor"/>, fuzzy-deduplicate against
/// in-memory state, and upsert to the graph store. Phase-4 dedup is a
/// simple lowercase-name match; Phase 5 / Ch 17 extends it with token
/// fuzzy-matching for the LazyGraphRAG indexer.
/// </summary>
public sealed class DocumentToGraphPipeline
{
    private readonly EntityExtractor _extractor;
    private readonly IGraphStore _store;

    public DocumentToGraphPipeline(EntityExtractor extractor, IGraphStore store)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        ArgumentNullException.ThrowIfNull(store);
        _extractor = extractor;
        _store = store;
    }

    public async Task IngestAsync(IEnumerable<DocumentChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        await _store.EnsureSchemaExistsAsync(cancellationToken).ConfigureAwait(false);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Entity resolution: a deliberately simple lowercase-exact dedup. It only
        // collapses names that match case-insensitively character-for-character,
        // so "Ada", "Ada Lovelace", and "A. Lovelace" fragment into separate
        // nodes — silently breaking multi-hop recall when a later chunk refers to
        // the same real-world entity by a different surface form. The production
        // upgrade is a fuzzy/token-match or vector-similarity resolver
        // (see the LazyGraphRAG indexer in Ch 17). Kept simple here on purpose.
        var nameToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extraction = await _extractor.ExtractAsync(chunk.Text, cancellationToken).ConfigureAwait(false);

            // Dedup entities by lowercase Name; reuse the canonical Id when found.
            foreach (var entity in extraction.Entities)
            {
                if (string.IsNullOrEmpty(entity.Name))
                {
                    continue;
                }
                if (nameToId.TryGetValue(entity.Name, out var canonicalId))
                {
                    if (!seenIds.Contains(canonicalId))
                    {
                        await _store.UpsertEntityAsync(entity with { Id = canonicalId }, cancellationToken).ConfigureAwait(false);
                        seenIds.Add(canonicalId);
                    }
                }
                else
                {
                    nameToId[entity.Name] = entity.Id;
                    await _store.UpsertEntityAsync(entity, cancellationToken).ConfigureAwait(false);
                    seenIds.Add(entity.Id);
                }
            }
            foreach (var rel in extraction.Relations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _store.UpsertRelationAsync(rel, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
