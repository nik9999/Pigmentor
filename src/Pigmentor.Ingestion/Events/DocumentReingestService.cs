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

    // documentId -> the chunk ids last written for it. Lets the service delete the
    // exact prior set (including orphans the new version no longer produces).
    private readonly ConcurrentDictionary<string, string[]> _lastIndexed =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Create the re-ingest service.
    /// </summary>
    /// <param name="rechunk">
    /// Produces the document's current desired chunks (read source, chunk it). In
    /// tests this is a stub returning a fixed set; in production it wraps the load +
    /// chunker path (<c>Pigmentor.Ingestion.Chunking.IChunker</c>).
    /// </param>
    /// <param name="embedder">The current embedding service.</param>
    /// <param name="store">The vector store to delete from and upsert into.</param>
    public DocumentReingestService(
        Func<string, CancellationToken, Task<IReadOnlyList<DocumentChunk>>> rechunk,
        IEmbeddingService embedder,
        IVectorStore store)
    {
        ArgumentNullException.ThrowIfNull(rechunk);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(store);
        _rechunk = rechunk;
        _embedder = embedder;
        _store = store;
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

        // 2. Delete ALL prior chunks for the document. This is the orphan-removal
        //    step: any old chunk id not produced by the new chunking would otherwise
        //    linger and keep being retrieved. We delete the union of (last-indexed
        //    set) and (new set) ids so a shrink leaves nothing behind.
        var oldIds = _lastIndexed.TryGetValue(documentId, out var prior) ? prior : [];
        var toDelete = new HashSet<string>(oldIds, StringComparer.Ordinal);
        foreach (var chunk in newChunks)
        {
            toDelete.Add(chunk.ChunkId);
        }

        if (toDelete.Count > 0)
        {
            await _store.DeleteAsync(toDelete, cancellationToken).ConfigureAwait(false);
        }

        // 3. Re-embed and insert the fresh set.
        var embedded = new List<EmbeddedChunk>(newChunks.Count);
        foreach (var chunk in newChunks)
        {
            embedded.Add(await _embedder.EmbedAsync(chunk, cancellationToken).ConfigureAwait(false));
        }

        if (embedded.Count > 0)
        {
            await _store.UpsertAsync(embedded, cancellationToken).ConfigureAwait(false);
        }

        // 4. Remember the new id set for the next re-ingest.
        _lastIndexed[documentId] = newChunks.Select(c => c.ChunkId).ToArray();
    }
}
