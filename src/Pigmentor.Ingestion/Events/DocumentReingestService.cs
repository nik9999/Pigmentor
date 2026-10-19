using System.Collections.Concurrent;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Events;

/// <summary>
/// Re-ingests a single document end to end (Ch 22 §6 #1 — the everyday
/// update/delete case). The signature the <see cref="IngestEventConsumer"/> calls
/// when a <see cref="DocumentChanged"/> event arrives.
/// </summary>
public interface IDocumentReingestService
{
    /// <summary>
    /// Re-chunk, re-embed, and re-index <paramref name="documentId"/>, removing the
    /// document's previous chunks first.
    /// </summary>
    Task ReingestAsync(string documentId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Re-ingest implementation that performs the <strong>delete-all-old-chunks then
/// insert-new</strong> sequence (Ch 22 §6 #1). Re-chunking shifts boundaries — the
/// new chunk count and offsets differ from the old ones — so a per-chunk-id upsert
/// would leave <em>orphan chunks</em>: stale slices of the previous version that no
/// new chunk overwrites and that keep getting retrieved. The fix is to delete every
/// prior chunk id for the document before inserting the freshly chunked set.
///
/// <para>
/// Chunk ids are the deterministic, stable <c>{DocumentId}#{ChunkIndex}</c> form, so
/// citations survive across a re-ingest wherever a chunk's position is unchanged.
/// The set of "the document's previous chunk ids" is read from an injected
/// re-chunk function (which returns the document's <em>current</em> desired chunks)
/// combined with a small in-process registry of what was last indexed — keeping the
/// service storage-agnostic and offline-testable (no enumerate-all API on
/// <see cref="IVectorStore"/>).
/// </para>
/// </summary>
public sealed class DocumentReingestService : IDocumentReingestService
{
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<DocumentChunk>>> _rechunk;
    private readonly IEmbeddingService _embedder;
    private readonly IVectorStore _store;
    private readonly IIngestionStateStore? _stateStore;

    // documentId -> the chunk ids last written for it. Serves as in-memory cache
    // and fallback when IIngestionStateStore is not configured.
    private readonly ConcurrentDictionary<string, string[]> _lastIndexed =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Create the re-ingest service with vector store and embedding service.
    /// </summary>
    public DocumentReingestService(
        Func<string, CancellationToken, Task<IReadOnlyList<DocumentChunk>>> rechunk,
        IEmbeddingService embedder,
        IVectorStore store)
        : this(rechunk, embedder, store, null)
    {
    }

    /// <summary>
    /// Create the re-ingest service with vector store, embedding service, and persistent ingestion state store.
    /// </summary>
    public DocumentReingestService(
        Func<string, CancellationToken, Task<IReadOnlyList<DocumentChunk>>> rechunk,
        IEmbeddingService embedder,
        IVectorStore store,
        IIngestionStateStore? stateStore)
    {
        ArgumentNullException.ThrowIfNull(rechunk);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(store);
        _rechunk = rechunk;
        _embedder = embedder;
        _store = store;
        _stateStore = stateStore;
    }

    /// <summary>
    /// Seed the service with the chunk ids already in the store for a document, so the
    /// first re-ingest knows what to delete. Optional — used when an index was built
    /// before this service was in the loop.
    /// </summary>
    public void RegisterExisting(string documentId, IEnumerable<string> chunkIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(chunkIds);
        _lastIndexed[documentId] = chunkIds.ToArray();
    }

    /// <inheritdoc />
    public async Task ReingestAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        // 1. Re-chunk the current document. Re-chunking can shift boundaries, so the
        //    new set may have a different count than what is in the index today.
        var newChunks = await _rechunk(documentId, cancellationToken).ConfigureAwait(false);

        // 2. Prepare new embeddings in memory BEFORE mutating vector store (recoverable boundary).
        var embedded = new List<EmbeddedChunk>(newChunks.Count);
        foreach (var chunk in newChunks)
        {
            embedded.Add(await _embedder.EmbedAsync(chunk, cancellationToken).ConfigureAwait(false));
        }

        // 3. Resolve prior chunk IDs: query persistent store first, then fallback to in-memory registry.
        IReadOnlyList<string> prior = [];
        IngestionDocumentState? priorState = null;
        if (_stateStore != null)
        {
            priorState = await _stateStore.GetAsync(documentId, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (priorState != null)
            {
                prior = priorState.ChunkIds;
            }
        }

        if (prior.Count == 0 && _lastIndexed.TryGetValue(documentId, out var cachedPrior))
        {
            prior = cachedPrior;
        }

        // Delete ALL prior chunks and new chunk IDs to prevent duplicates/orphans.
        var toDelete = new HashSet<string>(prior, StringComparer.Ordinal);
        foreach (var chunk in newChunks)
        {
            toDelete.Add(chunk.ChunkId);
        }

        if (toDelete.Count > 0)
        {
            await _store.DeleteAsync(toDelete, cancellationToken).ConfigureAwait(false);
        }

        // 4. Insert the fresh set into the vector store.
        if (embedded.Count > 0)
        {
            await _store.UpsertAsync(embedded, cancellationToken).ConfigureAwait(false);
        }

        var newChunkIds = newChunks.Select(c => c.ChunkId).ToArray();

        // 5. Commit persistent state if state store is configured.
        if (_stateStore != null)
        {
            var nextVersion = (priorState?.Version ?? 0) + 1;
            var updatedState = new IngestionDocumentState(
                SourceId: priorState?.SourceId ?? "default",
                DocumentId: documentId,
                ContentHash: priorState?.ContentHash ?? string.Empty,
                MetadataHash: priorState?.MetadataHash ?? string.Empty,
                Version: nextVersion,
                Status: IngestionStatus.Succeeded,
                LastSuccessfulIngestion: DateTimeOffset.UtcNow,
                ChunkIds: newChunkIds);

            await _stateStore.SaveAsync(updatedState, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // 6. Remember the new id set in the in-memory cache for subsequent re-ingests.
        _lastIndexed[documentId] = newChunkIds;
    }
}
