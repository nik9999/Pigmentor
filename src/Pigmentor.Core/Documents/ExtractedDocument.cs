namespace Pigmentor.Core.Documents;

/// <summary>
/// Intermediate structural representation of a document extracted from a source file before normalization.
/// </summary>
/// <param name="DocumentId">The unique identifier of the document.</param>
/// <param name="SourcePath">The original location of the source file.</param>
/// <param name="Format">The detected format extension (e.g. <c>.pdf</c>, <c>.docx</c>, <c>.xlsx</c>).</param>
/// <param name="Blocks">The ordered sequence of structural blocks extracted from the document.</param>
/// <param name="Metadata">Metadata associated with the document.</param>
public sealed record ExtractedDocument(
    string DocumentId,
    string SourcePath,
    string Format,
    IReadOnlyList<ExtractedBlock> Blocks,
    DocumentMetadata Metadata);

