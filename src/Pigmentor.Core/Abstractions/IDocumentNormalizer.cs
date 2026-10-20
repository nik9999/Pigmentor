using Pigmentor.Core.Documents;

namespace Pigmentor.Core.Abstractions;

/// <summary>
/// Normalization pipeline contract that cleans, dehyphenates, prunes noise, and enforces
/// quality gates on an extracted structural document.
/// </summary>
public interface IDocumentNormalizer
{
    /// <summary>
    /// Current schema and rule version of this normalizer. Changes to this value
    /// cause documents to be marked as changed for persistent reingestion.
    /// </summary>
    int NormalizationVersion { get; }

    /// <summary>
    /// Executes the normalization rules on the extracted document and returns the cleaned result.
    /// </summary>
    Task<NormalizedDocument> NormalizeAsync(
        ExtractedDocument document,
        CancellationToken cancellationToken = default);
}

