using Microsoft.Extensions.Logging;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Ingestion.Fingerprints;
using Pigmentor.Ingestion.Lifecycle;

namespace Pigmentor.Ingestion.Services;

/// <summary>
/// Production implementation of <see cref="IDocumentIngestionService"/> that delivers
/// restart-safe document lifecycle management, content/metadata deduplication, orphan chunk cleanup,
/// and source synchronization backed by <see cref="IIngestionStateStore"/>.
/// </summary>
public sealed partial class DocumentIngestionService : IDocumentIngestionService
{
    private readonly IIngestionStateStore _stateStore;
    private readonly IVectorStore _vectorStore;
    private readonly IChunker _chunker;
    private readonly IEmbeddingService _embedder;
    private readonly ILogger<DocumentIngestionService>? _logger;

    public DocumentIngestionService(
        IIngestionStateStore stateStore,
        IVectorStore vectorStore,
        IChunker chunker,
        IEmbeddingService embedder,
        ILogger<DocumentIngestionService>? logger = null)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _vectorStore = vectorStore ?? throw new ArgumentNullException(nameof(vectorStore));
        _chunker = chunker ?? throw new ArgumentNullException(nameof(chunker));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IngestionResult> IngestAsync(
        Document document,
        string sourceId = "default",
        long? explicitVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(document.Metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Metadata.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        var docId = document.Metadata.Id;
        var contentHash = IngestionFingerprint.ComputeContentHash(document.Content);
        var metadataHash = IngestionFingerprint.ComputeMetadataHash(document.Metadata);

        var existingState = await _stateStore.GetAsync(docId, sourceId, cancellationToken).ConfigureAwait(false);
        var changeKind = DocumentChangeClassifier.Classify(existingState, contentHash, metadataHash);

        if (changeKind == DocumentChangeKind.Unchanged && existingState is not null)
        {
            if (_logger != null)
            {
                Log.DocumentUnchanged(_logger, docId, sourceId);
            }
            return new IngestionResult(
                DocumentId: docId,
                ChangeKind: DocumentChangeKind.Unchanged,
                Status: IngestionStatus.Succeeded,
                Version: existingState.Version,
                ChunksIndexed: 0,
                ChunksDeleted: 0,
                Skipped: true);
        }

        long targetVersion;
        if (explicitVersion.HasValue)
        {
            targetVersion = explicitVersion.Value;
        }
        else if (existingState is null)
        {
            targetVersion = 1;
        }
        else
        {
            targetVersion = existingState.Version + 1;
        }

        // Record Processing status before starting mutations (recoverable boundary)
        var processingState = new IngestionDocumentState(
            SourceId: sourceId,
            DocumentId: docId,
            ContentHash: contentHash,
            MetadataHash: metadataHash,
            Version: targetVersion,
            Status: IngestionStatus.Processing,
            LastSuccessfulIngestion: existingState?.LastSuccessfulIngestion,
            ChunkIds: existingState?.ChunkIds ?? []);

        await _stateStore.SaveAsync(processingState, cancellationToken: cancellationToken).ConfigureAwait(false);

        try
        {
            // 1. Chunk document
            var chunks = new List<DocumentChunk>();
            await foreach (var chunk in _chunker.ChunkAsync(document, cancellationToken).ConfigureAwait(false))
            {
                chunks.Add(chunk);
            }

            // 2. Embed chunks in memory before touching vector store
            var embeddedChunks = new List<EmbeddedChunk>(chunks.Count);
            await foreach (var ec in _embedder.EmbedAsync(ToAsyncEnumerable(chunks), cancellationToken).ConfigureAwait(false))
            {
                embeddedChunks.Add(ec);
            }

            // 3. Delete prior chunks from vector store to avoid orphans
            var deletedCount = 0;
            if (existingState?.ChunkIds is { Count: > 0 } oldChunks)
            {
                await _vectorStore.DeleteAsync(oldChunks, cancellationToken).ConfigureAwait(false);
                deletedCount = oldChunks.Count;
            }

            // 4. Upsert new chunks into vector store
            if (embeddedChunks.Count > 0)
            {
                await _vectorStore.UpsertAsync(embeddedChunks, cancellationToken).ConfigureAwait(false);
            }

            // 5. Commit Succeeded state
            var newChunkIds = chunks.Select(c => c.ChunkId).ToList();
            var succeededState = new IngestionDocumentState(
                SourceId: sourceId,
                DocumentId: docId,
                ContentHash: contentHash,
                MetadataHash: metadataHash,
                Version: targetVersion,
                Status: IngestionStatus.Succeeded,
                LastSuccessfulIngestion: DateTimeOffset.UtcNow,
                ChunkIds: newChunkIds);

            await _stateStore.SaveAsync(succeededState, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (_logger != null)
            {
                Log.IngestSucceeded(_logger, docId, targetVersion, embeddedChunks.Count);
            }

            return new IngestionResult(
                DocumentId: docId,
                ChangeKind: changeKind,
                Status: IngestionStatus.Succeeded,
                Version: targetVersion,
                ChunksIndexed: embeddedChunks.Count,
                ChunksDeleted: deletedCount,
                Skipped: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_logger != null)
            {
                Log.IngestFailed(_logger, docId, targetVersion, ex);
            }
            try
            {
                var failedState = processingState with { Status = IngestionStatus.Failed };
                await _stateStore.SaveAsync(failedState, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Suppress secondary error when recording failure state
            }
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<SyncResult> SyncAsync(
        IEnumerable<Document> documents,
        string sourceId = "default",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        var sourceDocList = documents.ToList();
        var sourceDocMap = new Dictionary<string, Document>(StringComparer.Ordinal);
        foreach (var doc in sourceDocList)
        {
            ArgumentNullException.ThrowIfNull(doc);
            ArgumentNullException.ThrowIfNull(doc.Metadata);
            ArgumentException.ThrowIfNullOrWhiteSpace(doc.Metadata.Id);
            sourceDocMap[doc.Metadata.Id] = doc;
        }

        var existingStates = await _stateStore.ListAsync(sourceId, cancellationToken).ConfigureAwait(false);
        var deletedCount = 0;
        var results = new List<IngestionResult>();

        // 1. Detect and remove stale documents that are no longer in the source snapshot
        foreach (var state in existingStates)
        {
            if (!sourceDocMap.ContainsKey(state.DocumentId))
            {
                if (state.ChunkIds.Count > 0)
                {
                    await _vectorStore.DeleteAsync(state.ChunkIds, cancellationToken).ConfigureAwait(false);
                }
                await _stateStore.DeleteAsync(state.DocumentId, sourceId, cancellationToken).ConfigureAwait(false);
                deletedCount++;
            }
        }

        // 2. Ingest active source documents
        var ingestedCount = 0;
        var updatedCount = 0;
        var skippedCount = 0;

        foreach (var doc in sourceDocList)
        {
            var res = await IngestAsync(doc, sourceId, cancellationToken: cancellationToken).ConfigureAwait(false);
            results.Add(res);

            if (res.Skipped)
            {
                skippedCount++;
            }
            else if (res.ChangeKind == DocumentChangeKind.New)
            {
                ingestedCount++;
            }
            else
            {
                updatedCount++;
            }
        }

        return new SyncResult(
            SourceId: sourceId,
            TotalSourceDocuments: sourceDocList.Count,
            Ingested: ingestedCount,
            Updated: updatedCount,
            Skipped: skippedCount,
            Deleted: deletedCount,
            Results: results);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        string documentId,
        string sourceId = "default",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);

        var existing = await _stateStore.GetAsync(documentId, sourceId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        if (existing.ChunkIds.Count > 0)
        {
            await _vectorStore.DeleteAsync(existing.ChunkIds, cancellationToken).ConfigureAwait(false);
        }

        await _stateStore.DeleteAsync(documentId, sourceId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async IAsyncEnumerable<DocumentChunk> ToAsyncEnumerable(IEnumerable<DocumentChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
            Message = "Document {DocumentId} in source {SourceId} is unchanged; skipping re-indexing.")]
        public static partial void DocumentUnchanged(ILogger logger, string documentId, string sourceId);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information,
            Message = "Successfully ingested document {DocumentId} (v{Version}, {ChunksIndexed} chunks).")]
        public static partial void IngestSucceeded(ILogger logger, string documentId, long version, int chunksIndexed);

        [LoggerMessage(EventId = 3, Level = LogLevel.Error,
            Message = "Failed to ingest document {DocumentId} (v{Version}).")]
        public static partial void IngestFailed(ILogger logger, string documentId, long version, Exception exception);
    }
}
