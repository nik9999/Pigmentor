namespace Pigmentor.Core.Documents;

/// <summary>
/// A single source document loaded from disk (or any other source). The
/// content has not yet been chunked or embedded.
/// </summary>
/// <param name="Metadata">Front-matter metadata extracted at load time.</param>
/// <param name="Content">The raw textual body of the document, with front-matter stripped.</param>
/// <param name="SourcePath">
/// The original location the document was loaded from (e.g.
/// <c>data/hr-policies/hr-policies-001.md</c>). Useful for citation rendering
/// and re-ingest scenarios.
/// </param>
public sealed record Document(
    DocumentMetadata Metadata,
    string Content,
    string SourcePath);
