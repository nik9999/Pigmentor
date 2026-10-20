using Pigmentor.Core.Documents;

namespace Pigmentor.Core.Abstractions;

/// <summary>
/// Strategy contract for extracting structural documents from specific file extensions.
/// </summary>
public interface IFileExtractor
{
    /// <summary>The file extensions supported by this extractor (e.g. <c>.pdf</c>, <c>.xlsx</c>).</summary>
    IReadOnlySet<string> SupportedExtensions { get; }

    /// <summary>
    /// Extracts a structural <see cref="ExtractedDocument"/> from the provided readable stream.
    /// </summary>
    Task<ExtractedDocument> ExtractAsync(
        Stream stream,
        string filePath,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default);
}

