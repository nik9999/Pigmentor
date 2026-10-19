namespace Pigmentor.Core.Abstractions;

/// <summary>
/// Status of a document within the persistent ingestion lifecycle.
/// </summary>
public enum IngestionStatus
{
    /// <summary>Document parsing, chunking, or embedding is currently in progress.</summary>
    Processing,

    /// <summary>Document chunks and vectors have been successfully written and committed.</summary>
    Succeeded,

    /// <summary>Document ingestion failed during parsing, chunking, embedding, or writing.</summary>
    Failed
}

/// <summary>
/// Persistent state snapshot of an ingested document used for restart-safe lifecycle management,
/// change detection, orphan chunk deletion, and source synchronization.
/// </summary>
/// <param name="SourceId">Logical source or tenant partition identifier (default is "default").</param>
/// <param name="DocumentId">Stable dataset-unique document identifier.</param>
/// <param name="ContentHash">Deterministic hash of the raw document content (e.g. SHA-256).</param>
/// <param name="MetadataHash">Deterministic hash of the canonical metadata fields.</param>
/// <param name="Version">Monotonically increasing version number for the logical document.</param>
/// <param name="Status">Current ingestion lifecycle status.</param>
/// <param name="LastSuccessfulIngestion">UTC timestamp of the last successful ingestion, or null if not yet succeeded.</param>
/// <param name="ChunkIds">The collection of chunk IDs produced and indexed for this version of the document.</param>
public sealed record IngestionDocumentState(
    string SourceId,
    string DocumentId,
    string ContentHash,
    string MetadataHash,
    long Version,
    IngestionStatus Status,
    DateTimeOffset? LastSuccessfulIngestion,
    IReadOnlyList<string> ChunkIds);

/// <summary>
/// Storage-neutral abstraction for managing persistent ingestion state across process restarts.
/// Decoupled from vector indexing (<see cref="IVectorStore"/>) and relational SQL details.
/// </summary>
public interface IIngestionStateStore
{
    /// <summary>
    /// Retrieves the persisted ingestion state for <paramref name="documentId"/> within <paramref name="sourceId"/>.
    /// Returns <see langword="null"/> if the document has not yet been tracked.
    /// </summary>
    Task<IngestionDocumentState?> GetAsync(
        string documentId,
        string sourceId = "default",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all persisted document states associated with <paramref name="sourceId"/>.
    /// </summary>
    Task<IReadOnlyList<IngestionDocumentState>> ListAsync(
        string sourceId = "default",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves or updates the persisted ingestion state for a document.
    /// If <paramref name="expectedVersion"/> is specified, validates optimistic concurrency
    /// and throws <see cref="InvalidOperationException"/> if the persisted version differs.
    /// </summary>
    Task SaveAsync(
        IngestionDocumentState state,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the persisted ingestion state and associated chunk associations for <paramref name="documentId"/>.
    /// </summary>
    Task DeleteAsync(
        string documentId,
        string sourceId = "default",
        CancellationToken cancellationToken = default);
}

