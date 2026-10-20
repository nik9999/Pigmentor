using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Chunking;

/// <summary>
/// Row-aware chunker for structured tables that splits large tables across rows
/// while repeating column headers and metadata in every table chunk.
/// </summary>
public static class TableChunker
{
    public const int DefaultMaxRowsPerChunk = 30;

    public static IReadOnlyList<DocumentChunk> ChunkTable(
        TableBlock tableBlock,
        DocumentMetadata metadata,
        ref int chunkIndexCounter,
        int maxRowsPerChunk = DefaultMaxRowsPerChunk)
    {
        ArgumentNullException.ThrowIfNull(tableBlock);
        ArgumentNullException.ThrowIfNull(metadata);

        var table = tableBlock.Table;
        var totalRows = table.Rows.Count;

        if (totalRows <= maxRowsPerChunk)
        {
            var text = tableBlock.Text;
            var provStr = tableBlock.Provenance.ToCitationReference();
            var chunk = new DocumentChunk(
                ChunkId: FormattableString.Invariant($"{metadata.Id}#{chunkIndexCounter++}"),
                DocumentId: metadata.Id,
                ChunkIndex: chunkIndexCounter - 1,
                Text: text,
                StartCharOffset: 0,
                EndCharOffset: text.Length,
                Metadata: metadata)
            {
                Provenance = provStr
            };
            return [chunk];
        }

        var chunks = new List<DocumentChunk>();
        var baseRowStart = tableBlock.Provenance.RowStart ?? 2;

        for (var start = 0; start < totalRows; start += maxRowsPerChunk)
        {
            var count = Math.Min(maxRowsPerChunk, totalRows - start);
            var sliceRows = table.Rows.Skip(start).Take(count).ToList();

            var subTable = table with
            {
                Rows = sliceRows
            };

            var chunkText = subTable.ToMarkdownTable();
            var rowStart = baseRowStart + start;
            var rowEnd = baseRowStart + start + count - 1;

            var sliceProvenance = tableBlock.Provenance with
            {
                RowStart = rowStart,
                RowEnd = rowEnd
            };

            var chunk = new DocumentChunk(
                ChunkId: FormattableString.Invariant($"{metadata.Id}#{chunkIndexCounter++}"),
                DocumentId: metadata.Id,
                ChunkIndex: chunkIndexCounter - 1,
                Text: chunkText,
                StartCharOffset: 0,
                EndCharOffset: chunkText.Length,
                Metadata: metadata)
            {
                Provenance = sliceProvenance.ToCitationReference()
            };

            chunks.Add(chunk);
        }

        return chunks;
    }
}

