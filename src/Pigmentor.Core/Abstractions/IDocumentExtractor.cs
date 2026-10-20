using Pigmentor.Core.Documents;

namespace Pigmentor.Core.Abstractions;

/// <summary>
/// Top-level coordinator for extracting structural documents across all supported formats.
/// </summary>
public interface IDocumentExtractor
{
    /// <summary>Determines whether the given file extension is supported by any registered extractor.</summary>
    bool CanExtract(string extension);

    /// <summary>
    /// Extracts a structural document from a readable stream.
    /// </summary>
    Task<ExtractedDocument> ExtractAsync(
        Stream stream,
        string filePath,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts a structural document from a local file path.
    /// </summary>
    Task<ExtractedDocument> ExtractFileAsync(
        string filePath,
        DocumentMetadata? metadata = null,
        CancellationToken cancellationToken = default);
}

