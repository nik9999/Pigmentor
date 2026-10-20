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
    private readonly IDocumentExtractor? _documentExtractor;
    private readonly IDocumentNormalizer? _documentNormalizer;
    private readonly ILogger<DocumentIngestionService>? _logger;

    public DocumentIngestionService(
        IIngestionStateStore stateStore,
        IVectorStore vectorStore,
        IChunker chunker,
        IEmbeddingService embedder,
        IDocumentExtractor? documentExtractor = null,
        IDocumentNormalizer? documentNormalizer = null,
        ILogger<DocumentIngestionService>? logger = null)
    {
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _vectorStore = vectorStore ?? throw new ArgumentNullException(nameof(vectorStore));
        _chunker = chunker ?? throw new ArgumentNullException(nameof(chunker));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _documentExtractor = documentExtractor;
        _documentNormalizer = documentNormalizer;
        _logger = logger;
    }

    public DocumentIngestionService(
        IIngestionStateStore stateStore,
        IVectorStore vectorStore,
        IChunker chunker,
        IEmbeddingService embedder,
        ILogger<DocumentIngestionService>? logger)
        : this(stateStore, vectorStore, chunker, embedder, null, null, logger)
    {
    }

    /// <inheritdoc />
    public async Task<IngestionResult> IngestFileAsync(
        string filePath,
        DocumentMetadata? metadata = null,
        string sourceId = "default",
        long? explicitVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (_documentExtractor == null || _documentNormalizer == null)
        {
            throw new InvalidOperationException(
                "Document extractor or normalizer is not configured on DocumentIngestionService. Register them via AddDocumentExtraction().");
        }

        var extracted = await _documentExtractor.ExtractFileAsync(filePath, metadata, cancellationToken).ConfigureAwait(false);
        var normalized = await _documentNormalizer.NormalizeAsync(extracted, cancellationToken).ConfigureAwait(false);

        var doc = new Document(
            Metadata: normalized.Metadata,
            Content: normalized.ToFullContent(),
            SourcePath: filePath);

        return await IngestAsync(doc, sourceId, explicitVersion, cancellationToken).ConfigureAwait(false);
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

        // Enforce global DocumentId uniqueness per database across synchronization scopes (§21, §26)
        var existingSourceId = await _stateStore.GetSourceIdAsync(docId, cancellationToken).ConfigureAwait(false);
        if (existingSourceId != null && !string.Equals(existingSourceId, sourceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Document '{docId}' is already registered in synchronization scope '{existingSourceId}', cannot ingest under scope '{sourceId}'. DocumentId must be globally unique per database.");
        }

        var existingState = await _stateStore.GetAsync(docId, sourceId, cancellationToken).ConfigureAwait(false);
        var changeKind = DocumentChangeClassifier.Classify(existingState, contentHash, metadataHash);

        if (changeKind == DocumentChangeKind.Unchanged && existingState is not null)
        {
            if (explicitVersion.HasValue && explicitVersion.Value < existingState.Version)
            {
                throw new InvalidOperationException(
                    $"Cannot ingest document '{docId}' with explicit version {explicitVersion.Value} because stored version is {existingState.Version}. Downgrading version is rejected.");
            }

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
        long expectedVersion;
        IngestionStatus? expectedStatus;

        if (explicitVersion.HasValue)
        {
            var ev = explicitVersion.Value;
            if (ev <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(explicitVersion), "Explicit version must be positive.");
            }

            if (existingState is not null)
            {
                if (ev < existingState.Version)
                {
                    throw new InvalidOperationException(
                        $"Cannot ingest document '{docId}' with explicit version {ev} because stored version is {existingState.Version}. Downgrading version is rejected.");
                }

                if (ev == existingState.Version)
                {
                    if (existingState.Status == IngestionStatus.Succeeded)
                    {
                        throw new InvalidOperationException(
                            $"Cannot ingest changed document '{docId}' with duplicate explicit version {ev} which is already in Succeeded status.");
                    }

                    // Retry of incomplete/failed attempt at explicit version
                    targetVersion = ev;
                    expectedVersion = existingState.Version;
                    expectedStatus = existingState.Status;
                }
                else
                {
                    targetVersion = ev;
                    expectedVersion = existingState.Version;
                    expectedStatus = existingState.Status;
                }
            }
            else
            {
                targetVersion = ev;
                expectedVersion = 0;
                expectedStatus = null;
            }
        }
        else
        {
            if (existingState is null)
            {
                targetVersion = 1;
                expectedVersion = 0;
                expectedStatus = null;
            }
            else if (existingState.Status != IngestionStatus.Succeeded)
            {
                // Deterministic retry semantics: retry the same version without artificially inflating version numbers (§34)
                targetVersion = existingState.Version;
                expectedVersion = existingState.Version;
                expectedStatus = existingState.Status;
            }
            else
            {
                targetVersion = existingState.Version + 1;
                expectedVersion = existingState.Version;
                expectedStatus = IngestionStatus.Succeeded;
            }
        }

        // Atomic CAS claim: write Processing status before mutating vector store (recoverable boundary)
        var processingState = new IngestionDocumentState(
            SourceId: sourceId,
            DocumentId: docId,
            ContentHash: contentHash,
            MetadataHash: metadataHash,
            Version: targetVersion,
            Status: IngestionStatus.Processing,
            LastSuccessfulIngestion: existingState?.LastSuccessfulIngestion,
            ChunkIds: existingState?.ChunkIds ?? []);

        await _stateStore.SaveAsync(
            processingState,
            expectedVersion: expectedVersion,
            expectedStatus: expectedStatus,
            cancellationToken: cancellationToken).ConfigureAwait(false);

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

            // 5. Guarded commit: Transition Processing -> Succeeded only if this attempt is still current
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

            await _stateStore.SaveAsync(
                succeededState,
                expectedVersion: targetVersion,
                expectedStatus: IngestionStatus.Processing,
                cancellationToken: cancellationToken).ConfigureAwait(false);

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
        catch (OperationCanceledException)
        {
            // Controlled cancellation: leave Processing state behind for restart/retry recovery (§8)
            if (_logger != null)
            {
                Log.IngestionCanceled(_logger, docId, targetVersion);
            }
            throw;
        }
        catch (Exception ex)
        {
            if (_logger != null)
            {
                Log.IngestFailed(_logger, docId, targetVersion, ex);
            }
            try
            {
                // Conditional failure transition: only mark Failed if still in Processing at targetVersion (§7)
                var failedState = processingState with { Status = IngestionStatus.Failed };
                await _stateStore.SaveAsync(
                    failedState,
                    expectedVersion: targetVersion,
                    expectedStatus: IngestionStatus.Processing,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception saveEx)
            {
                if (_logger != null)
                {
                    Log.RecordFailedStatusFailed(_logger, docId, targetVersion, saveEx);
                }
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

        [LoggerMessage(EventId = 4, Level = LogLevel.Information,
            Message = "Ingestion for document {DocumentId} (v{Version}) was canceled; leaving Processing state for restart recovery.")]
        public static partial void IngestionCanceled(ILogger logger, string documentId, long version);

        [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
            Message = "Failed to record Failed status for document {DocumentId} (v{Version}). Primary exception will be rethrown.")]
        public static partial void RecordFailedStatusFailed(ILogger logger, string documentId, long version, Exception exception);
    }
}
