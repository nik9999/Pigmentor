namespace Pigmentor.Core.Documents;

/// <summary>
/// Cleaned, normalized, and validated structural representation of a document ready for chunking and embeddings.
/// </summary>
public sealed record NormalizedDocument(
    string DocumentId,
    string SourcePath,
    string Format,
    IReadOnlyList<ExtractedBlock> Blocks,
    DocumentMetadata Metadata,
    NormalizationDiagnostics Diagnostics,
    int NormalizationVersion = 1)
{
    /// <summary>
    /// Concatenates all block text in document order for baseline whole-document content hashing or preview.
    /// </summary>
    public string ToFullContent() => string.Join("\n\n", Blocks.Select(b => b.Text));
}

