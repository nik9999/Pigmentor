namespace Pigmentor.Ingestion.Events;

/// <summary>
/// A change-feed event signaling that a document was created, edited, or deleted
/// and must be re-ingested (Ch 22). Carries the tenant for tenant-scoped handling
/// and a monotonically increasing <see cref="Version"/> for ordering: a consumer
/// drops any event whose version is at or below the last one it processed for the
/// same document, which makes out-of-order and duplicate delivery safe.
/// </summary>
/// <param name="DocumentId">The id of the document that changed.</param>
/// <param name="TenantId">The tenant the document belongs to.</param>
/// <param name="Version">
/// The document's version at the time of the change. Strictly increasing per
/// document; used for version-guarded ordering.
/// </param>
public sealed record DocumentChanged(string DocumentId, string TenantId, long Version);
