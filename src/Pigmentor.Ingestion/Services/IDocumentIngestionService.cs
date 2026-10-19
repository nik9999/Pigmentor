using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Ingestion.Lifecycle;

namespace Pigmentor.Ingestion.Services;

/// <summary>
/// Result of an individual document ingestion attempt.
/// </summary>
/// <param name="DocumentId">Stable document identifier.</param>
/// <param name="ChangeKind">Classification of the change detected against persistent state.</param>
/// <param name="Status">Final ingestion status.</param>
/// <param name="Version">The document version associated with this ingestion state.</param>
/// <param name="ChunksIndexed">Number of new chunks written to the vector store.</param>
/// <param name="ChunksDeleted">Number of old/orphan chunks deleted from the vector store.</param>
/// <param name="Skipped">True if the document was unchanged and skipped expensive reprocessing.</param>
public sealed record IngestionResult(
    string DocumentId,
    DocumentChangeKind ChangeKind,
    IngestionStatus Status,
    long Version,
    int ChunksIndexed,
    int ChunksDeleted,
    bool Skipped);

/// <summary>
/// Result of a bulk synchronization between a source document collection and the persistent state.
/// </summary>
/// <param name="SourceId">Logical source or partition identifier.</param>
/// <param name="TotalSourceDocuments">Total count of source documents processed in the sync snapshot.</param>
/// <param name="Ingested">Count of new documents ingested.</param>
/// <param name="Updated">Count of changed documents updated.</param>
/// <param name="Skipped">Count of unchanged documents skipped.</param>
/// <param name="Deleted">Count of stale documents removed from the index and state.</param>
/// <param name="Results">Detailed ingestion results for each processed document.</param>
public sealed record SyncResult(
    string SourceId,
    int TotalSourceDocuments,
    int Ingested,
    int Updated,
    int Skipped,
    int Deleted,
    IReadOnlyList<IngestionResult> Results);

/// <summary>
/// High-level restart-safe ingestion service that coordinates persistent ingestion state,
/// chunking, embedding, vector store upsertion, and orphan chunk cleanup.
/// </summary>
public interface IDocumentIngestionService
{
    /// <summary>
    /// Ingests a single document, skipping reprocessing if content and metadata are unchanged.
    /// If changed, removes any previous chunks to eliminate orphan slices and records updated state.
    /// </summary>
    Task<IngestionResult> IngestAsync(
        Document document,
        string sourceId = "default",
        long? explicitVersion = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronizes a snapshot of source documents against the persistent index.
    /// Ingests new documents, replaces changed documents, skips unchanged documents,
    /// and deletes any previously indexed documents that are missing from the snapshot.
    /// </summary>
    Task<SyncResult> SyncAsync(
        IEnumerable<Document> documents,
        string sourceId = "default",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a document, removing its chunks from the vector store and its record from the state store.
    /// </summary>
    Task<bool> DeleteAsync(
        string documentId,
        string sourceId = "default",
        CancellationToken cancellationToken = default);
}

