using System.Text;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Multimodal;

/// <summary>
/// Chunker decorator that augments each text chunk with the captions of
/// any <see cref="ExtractedImage"/>s and the markdown of any
/// <see cref="ExtractedTable"/>s assigned to that chunk's page range.
/// Image captions and table markdown are inlined into the chunk text so
/// downstream embedding + retrieval pick them up unchanged.
/// </summary>
public sealed class MultimodalChunker
{
    private readonly IChunker _innerTextChunker;
    private readonly Dictionary<int, IReadOnlyList<ExtractedImage>> _imagesByPage;
    private readonly Dictionary<int, IReadOnlyList<ExtractedTable>> _tablesByPage;

    public MultimodalChunker(
        IChunker innerTextChunker,
        IReadOnlyList<ExtractedImage> images,
        IReadOnlyList<ExtractedTable> tables)
    {
        ArgumentNullException.ThrowIfNull(innerTextChunker);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(tables);

        _innerTextChunker = innerTextChunker;
        _imagesByPage = images.GroupBy(i => i.PageNumber)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ExtractedImage>)g.ToList());
        _tablesByPage = tables.GroupBy(t => t.PageNumber)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ExtractedTable>)g.ToList());
    }

    /// <summary>
    /// Build a stream of <see cref="MultimodalChunk"/>s. The simple page-based
    /// assignment used here treats every text chunk as "page 1" unless the
    /// caller supplies a page-aware text chunker (e.g. a future PDF text
    /// chunker that emits per-page chunks). For Phase 2 / Ch 5 the demo
    /// data — Markdown stand-ins for the financial-reports silo — has a
    /// single page.
    /// </summary>
    public async IAsyncEnumerable<MultimodalChunk> ChunkAsync(
        Document document,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        await foreach (var raw in _innerTextChunker.ChunkAsync(document, cancellationToken).ConfigureAwait(false))
        {
            // For Phase 2 we treat the whole document as page 1 — chapters
            // 5 and 17 will plug in a page-aware chunker.
            const int pageNumber = 1;
            var imgs = _imagesByPage.TryGetValue(pageNumber, out var imageList)
                ? imageList : (IReadOnlyList<ExtractedImage>)Array.Empty<ExtractedImage>();
            var tbls = _tablesByPage.TryGetValue(pageNumber, out var tableList)
                ? tableList : (IReadOnlyList<ExtractedTable>)Array.Empty<ExtractedTable>();

            var augmented = BuildAugmentedText(raw.Text, imgs, tbls);
            var augmentedChunk = raw with { Text = augmented };
            yield return new MultimodalChunk(augmentedChunk, imgs, tbls);
        }
    }

    private static string BuildAugmentedText(
        string text,
        IReadOnlyList<ExtractedImage> images,
        IReadOnlyList<ExtractedTable> tables)
    {
        if (images.Count == 0 && tables.Count == 0)
        {
            return text;
        }

        var sb = new StringBuilder(text);
        foreach (var img in images)
        {
            if (string.IsNullOrWhiteSpace(img.Caption))
            {
                continue;
            }
            sb.Append("\n\n[Figure ").Append(img.ImageId).Append("] ").Append(img.Caption);
        }
        foreach (var table in tables)
        {
            sb.Append("\n\n[Table ").Append(table.TableId).Append("]\n").Append(table.Markdown);
        }
        return sb.ToString();
    }
}
