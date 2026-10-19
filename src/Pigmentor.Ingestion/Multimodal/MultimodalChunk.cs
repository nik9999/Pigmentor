using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Multimodal;

/// <summary>
/// A chunk that, in addition to text, references one or more extracted
/// images (charts, diagrams, scanned figures) and structured tables. The
/// chunk's <see cref="DocumentChunk.Text"/> embeds image captions and
/// markdown-rendered tables so the existing dense-retrieval path picks
/// it up unmodified.
/// </summary>
public sealed record MultimodalChunk(
    DocumentChunk Chunk,
    IReadOnlyList<ExtractedImage> Images,
    IReadOnlyList<ExtractedTable> Tables);

/// <param name="ImageId">Stable id of the form <c>{DocumentId}#img-{n}</c>.</param>
/// <param name="PageNumber">1-based PDF page number.</param>
/// <param name="Caption">LLM- or vision-model-generated description of the image.</param>
/// <param name="ImagePath">Path on disk where the extracted bitmap was written, or null if extraction was caption-only.</param>
public sealed record ExtractedImage(
    string ImageId,
    int PageNumber,
    string Caption,
    string? ImagePath);

/// <param name="TableId">Stable id of the form <c>{DocumentId}#tbl-{n}</c>.</param>
/// <param name="PageNumber">1-based PDF page number.</param>
/// <param name="Markdown">The table rendered as a Markdown table — the format LLMs handle most reliably.</param>
public sealed record ExtractedTable(
    string TableId,
    int PageNumber,
    string Markdown);
