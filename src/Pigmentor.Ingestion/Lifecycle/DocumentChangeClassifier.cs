using Pigmentor.Core.Abstractions;

namespace Pigmentor.Ingestion.Lifecycle;

/// <summary>
/// Classification of document changes compared to the persisted ingestion state.
/// </summary>
public enum DocumentChangeKind
{
    /// <summary>Document has not yet been indexed or previous ingestion was interrupted/failed.</summary>
    New,

    /// <summary>Both content and metadata match the persisted succeeded state; can safely skip processing.</summary>
    Unchanged,

    /// <summary>Content has changed; metadata is unchanged.</summary>
    ContentChanged,

    /// <summary>Metadata has changed; content is unchanged.</summary>
    MetadataChanged,

    /// <summary>Both content and metadata have changed.</summary>
    ContentAndMetadataChanged
}

/// <summary>
/// Classifies document state transitions against persisted state.
/// </summary>
public static class DocumentChangeClassifier
{
    /// <summary>
    /// Determines the change kind for a document given its persisted state and current hashes.
    /// Invariant: An incomplete state (Processing or Failed) is always classified as <see cref="DocumentChangeKind.New"/>
    /// to ensure interrupted ingestions are fully repaired.
    /// </summary>
    public static DocumentChangeKind Classify(
        IngestionDocumentState? existingState,
        string currentContentHash,
        string currentMetadataHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentContentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentMetadataHash);

        if (existingState == null || existingState.Status != IngestionStatus.Succeeded)
        {
            return DocumentChangeKind.New;
        }

        var contentMatches = string.Equals(existingState.ContentHash, currentContentHash, StringComparison.Ordinal);
        var metadataMatches = string.Equals(existingState.MetadataHash, currentMetadataHash, StringComparison.Ordinal);

        if (contentMatches && metadataMatches)
        {
            return DocumentChangeKind.Unchanged;
        }

        if (!contentMatches && metadataMatches)
        {
            return DocumentChangeKind.ContentChanged;
        }

        if (contentMatches && !metadataMatches)
        {
            return DocumentChangeKind.MetadataChanged;
        }

        return DocumentChangeKind.ContentAndMetadataChanged;
    }
}

