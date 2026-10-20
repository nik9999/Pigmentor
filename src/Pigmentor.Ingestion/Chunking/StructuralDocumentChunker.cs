using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Chunking;

/// <summary>
/// Structure-aware chunker that respects document boundaries: tables are chunked by rows
/// with repeated headers, figures form isolated chunks with provenance, and prose
/// is chunked without splitting headings or mid-sentence.
/// </summary>
public sealed class StructuralDocumentChunker : IChunker
{
    private readonly int _maxRowsPerTableChunk;
    private readonly int _maxProseChunkChars;

    public StructuralDocumentChunker(
        int maxRowsPerTableChunk = TableChunker.DefaultMaxRowsPerChunk,
        int maxProseChunkChars = 1500)
    {
        _maxRowsPerTableChunk = maxRowsPerTableChunk;
        _maxProseChunkChars = maxProseChunkChars;
    }

    public string Strategy => "structural";

    /// <summary>
    /// Chunks a pre-normalized structural document into deterministic chunks.
    /// </summary>
    public IReadOnlyList<DocumentChunk> Chunk(NormalizedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var chunks = new List<DocumentChunk>();
        var chunkIndexCounter = 0;
        var metadata = document.Metadata;

        var proseBuffer = new List<ExtractedBlock>();
        var proseCharCount = 0;

        void FlushProseBuffer()
        {
            if (proseBuffer.Count == 0)
            {
                return;
            }

            var text = string.Join("\n\n", proseBuffer.Select(b => b.Text)).Trim();
            if (!string.IsNullOrEmpty(text))
            {
                var firstProv = proseBuffer[0].Provenance;
                var lastProv = proseBuffer[^1].Provenance;

                var combinedProv = firstProv with
                {
                    PageEnd = lastProv.PageNumber ?? firstProv.PageNumber
                };

                var chunk = new DocumentChunk(
                    ChunkId: FormattableString.Invariant($"{metadata.Id}#{chunkIndexCounter++}"),
                    DocumentId: metadata.Id,
                    ChunkIndex: chunkIndexCounter - 1,
                    Text: text,
                    StartCharOffset: 0,
                    EndCharOffset: text.Length,
                    Metadata: metadata)
                {
                    Provenance = combinedProv.ToCitationReference()
                };

                chunks.Add(chunk);
            }

            proseBuffer.Clear();
            proseCharCount = 0;
        }

        foreach (var block in document.Blocks)
        {
            if (block is TableBlock tb)
            {
                FlushProseBuffer();
                var tableChunks = TableChunker.ChunkTable(tb, metadata, ref chunkIndexCounter, _maxRowsPerTableChunk);
                chunks.AddRange(tableChunks);
            }
            else if (block is FigureBlock fb)
            {
                FlushProseBuffer();
                var chunk = new DocumentChunk(
                    ChunkId: FormattableString.Invariant($"{metadata.Id}#{chunkIndexCounter++}"),
                    DocumentId: metadata.Id,
                    ChunkIndex: chunkIndexCounter - 1,
                    Text: fb.Text,
                    StartCharOffset: 0,
                    EndCharOffset: fb.Text.Length,
                    Metadata: metadata)
                {
                    Provenance = fb.Provenance.ToCitationReference()
                };
                chunks.Add(chunk);
            }
            else
            {
                var blockLength = block.Text.Length;
                if (proseCharCount + blockLength > _maxProseChunkChars && proseBuffer.Count > 0)
                {
                    FlushProseBuffer();
                }

                proseBuffer.Add(block);
                proseCharCount += blockLength;
            }
        }

        FlushProseBuffer();
        return chunks;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DocumentChunk> ChunkAsync(
        Document document,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Fallback for raw Document: split paragraphs into TextBlocks and chunk structurally
        var paragraphs = document.Content
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var blocks = new List<ExtractedBlock>(paragraphs.Length);
        for (var i = 0; i < paragraphs.Length; i++)
        {
            var p = paragraphs[i];
            var prov = new BlockProvenance(document.Metadata.Id, PageNumber: 1);
            if (p.StartsWith("# ", StringComparison.Ordinal) || p.StartsWith("## ", StringComparison.Ordinal))
            {
                blocks.Add(new HeadingBlock(FormattableString.Invariant($"{document.Metadata.Id}#b{i}"), p, 1, prov));
            }
            else
            {
                blocks.Add(new TextBlock(FormattableString.Invariant($"{document.Metadata.Id}#b{i}"), p, prov));
            }
        }

        var normalizedDoc = new NormalizedDocument(
            DocumentId: document.Metadata.Id,
            SourcePath: document.SourcePath,
            Format: Path.GetExtension(document.SourcePath),
            Blocks: blocks,
            Metadata: document.Metadata,
            Diagnostics: new NormalizationDiagnostics());

        var chunkList = Chunk(normalizedDoc);
        foreach (var chunk in chunkList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return chunk;
        }

        await Task.CompletedTask;
    }
}

