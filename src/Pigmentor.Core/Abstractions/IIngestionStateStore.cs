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
/// Exception thrown when an optimistic concurrency check fails during an ingestion state transition.
/// </summary>
public sealed class IngestionConcurrencyException : Exception
{
    public IngestionConcurrencyException(string message)
        : base(message)
    {
    }

    public IngestionConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Persistent state snapshot of an ingested document used for restart-safe lifecycle management,
/// change detection, orphan chunk deletion, and source synchronization.
///
/// <para>
/// <strong>Semantics across statuses:</strong><br/>
/// - For <see cref="IngestionStatus.Succeeded"/>, <see cref="ChunkIds"/> match the active chunks in the vector store.<br/>
/// - For <see cref="IngestionStatus.Processing"/> and <see cref="IngestionStatus.Failed"/>, <see cref="ChunkIds"/> represent
/// the last known chunk cleanup/reconciliation set from the prior successful state, and <see cref="LastSuccessfulIngestion"/>
/// retains the timestamp of the last successful indexing attempt (null if never succeeded).
/// </para>
/// </summary>
/// <param name="SourceId">
/// Logical ingestion source / synchronization scope. Does not provide tenant, security, or vector namespace isolation.
/// <paramref name="DocumentId"/> must be globally unique within one Pigmentor database.
/// </param>
/// <param name="DocumentId">Stable globally unique document identifier within the database.</param>
/// <param name="ContentHash">Deterministic hash of the raw document content (e.g. SHA-256).</param>
/// <param name="MetadataHash">Deterministic hash of the canonical metadata fields.</param>
/// <param name="Version">Monotonically increasing version number for the logical document.</param>
/// <param name="Status">Current ingestion lifecycle status.</param>
/// <param name="LastSuccessfulIngestion">UTC timestamp of the last successful ingestion, or null if not yet succeeded.</param>
/// <param name="ChunkIds">The collection of chunk IDs produced and indexed for this version of the document (or prior cleanup set).</param>
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
///
/// <para>
/// Ingestion operations across the vector store and state store are <strong>RECOVERABLE BUT NOT FULLY ATOMIC</strong>.
/// If an operation is interrupted or crashes, the persisted <see cref="IngestionStatus.Processing"/> or
/// <see cref="IngestionStatus.Failed"/> state ensures deterministic repair on subsequent restart or synchronization.
/// </para>
/// </summary>
public interface IIngestionStateStore
{
    /// <summary>
    /// Retrieves the persisted ingestion state for <paramref name="documentId"/> within <paramref name="sourceId"/>.
    /// Returns <see langword="null"/> if the document has not yet been tracked in this scope.
    /// </summary>
    Task<IngestionDocumentState?> GetAsync(
        string documentId,
        string sourceId = "default",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the synchronization scope (<c>source_id</c>) under which <paramref name="documentId"/> is currently registered,
    /// or <see langword="null"/> if the document does not exist in any scope.
    /// Used to enforce the global uniqueness of <c>DocumentId</c> across scopes within the database.
    /// </summary>
    Task<string?> GetSourceIdAsync(
        string documentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all persisted document states associated with <paramref name="sourceId"/>.
    /// </summary>
    Task<IReadOnlyList<IngestionDocumentState>> ListAsync(
        string sourceId = "default",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves or updates the persisted ingestion state for a document using compare-and-swap semantics.
    /// <list type="bullet">
    ///   <item><description>If <paramref name="expectedVersion"/> is 0, claims a brand new document; throws <see cref="IngestionConcurrencyException"/> if it already exists.</description></item>
    ///   <item><description>If <paramref name="expectedVersion"/> is positive, verifies matching version (and optionally <paramref name="expectedStatus"/>); throws <see cref="IngestionConcurrencyException"/> on concurrency mismatch.</description></item>
    ///   <item><description>If <paramref name="expectedVersion"/> is null, performs unconditional upsert.</description></item>
    /// </list>
    /// </summary>
    Task SaveAsync(
        IngestionDocumentState state,
        long? expectedVersion = null,
        IngestionStatus? expectedStatus = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the persisted ingestion state and associated chunk associations for <paramref name="documentId"/>.
    /// </summary>
    Task DeleteAsync(
        string documentId,
        string sourceId = "default",
        CancellationToken cancellationToken = default);
}
