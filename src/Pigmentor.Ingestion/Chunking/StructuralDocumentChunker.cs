using System.Text.RegularExpressions;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Chunking;

/// <summary>
/// Structure-aware chunker that respects document boundaries: tables are chunked by rows
/// with repeated headers and contextual ancestry prefixes, figures form isolated chunks with provenance,
/// and prose is chunked without splitting headings or mid-sentence.
/// </summary>
public sealed class StructuralDocumentChunker : IChunker
{
    private static readonly Regex SectionHeadingRegex = new(@"^\d+\s+[A-ZА-ЯЁ]", RegexOptions.Compiled);
    private static readonly Regex NumberedHeadingRegex = new(@"^\d+(\.\d+)*\s+[A-ZА-ЯЁ][^\n\.\r]{2,80}", RegexOptions.Compiled);

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

        // 1. Identify document/device title from page 1/2 or metadata
        var page1Headings = document.Blocks
            .Where(b => (b.Provenance.PageNumber ?? 1) <= 2 && b is HeadingBlock)
            .Select(b => b.Text.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length < 100)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        string docTitle;
        if (page1Headings.Count > 0)
        {
            docTitle = string.Join(" / ", page1Headings);
        }
        else if (!string.IsNullOrWhiteSpace(metadata.Title))
        {
            docTitle = metadata.Title;
        }
        else
        {
            docTitle = metadata.Id;
        }

        string? currentSection = null;
        string? currentSubsection = null;
        string? recentTableIntro = null;

        for (var i = 0; i < document.Blocks.Count; i++)
        {
            var block = document.Blocks[i];

            if (block is HeadingBlock hb)
            {
                var hText = hb.Text.Trim();
                if (hb.Level == 1 || SectionHeadingRegex.IsMatch(hText))
                {
                    currentSection = hText;
                    currentSubsection = null;
                    recentTableIntro = null;
                }
                else
                {
                    currentSubsection = hText;
                    recentTableIntro = null;
                }
            }
            else if (block is TextBlock tb)
            {
                var tText = tb.Text.Trim();
                var secMatch = NumberedHeadingRegex.Match(tText);
                if (secMatch.Success)
                {
                    var headingLine = secMatch.Value.Trim();
                    if (headingLine.Contains('.'))
                    {
                        currentSubsection = headingLine;
                    }
                    else
                    {
                        currentSection = headingLine;
                        currentSubsection = null;
                    }
                }

                if (tText.Contains("таблиц", StringComparison.OrdinalIgnoreCase))
                {
                    recentTableIntro = ExtractConciseTableIntro(tText);
                }
            }

            if (block is TableBlock tableBlock)
            {
                FlushProseBuffer();

                // Look for caption in table itself, or in adjacent block
                var caption = tableBlock.Table.Caption;
                if (string.IsNullOrWhiteSpace(caption) || caption.StartsWith("Таблица 1", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < document.Blocks.Count &&
                        document.Blocks[i + 1] is TextBlock or HeadingBlock &&
                        document.Blocks[i + 1].Text.Trim().StartsWith("Таблица", StringComparison.OrdinalIgnoreCase))
                    {
                        caption = document.Blocks[i + 1].Text.Trim();
                    }
                }

                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(docTitle))
                {
                    parts.Add(docTitle);
                }
                if (!string.IsNullOrWhiteSpace(currentSection) && !parts.Contains(currentSection))
                {
                    parts.Add(currentSection);
                }
                if (!string.IsNullOrWhiteSpace(currentSubsection) && !parts.Contains(currentSubsection))
                {
                    parts.Add(currentSubsection);
                }
                if (!string.IsNullOrWhiteSpace(caption) && !parts.Contains(caption))
                {
                    parts.Add(caption);
                }
                else if (!string.IsNullOrWhiteSpace(recentTableIntro) && !parts.Contains(recentTableIntro))
                {
                    parts.Add(recentTableIntro);
                }

                var contextHeader = parts.Count > 0 ? $"[Контекст: {string.Join(" — ", parts)}]" : null;

                var tableChunks = TableChunker.ChunkTable(tableBlock, metadata, ref chunkIndexCounter, _maxRowsPerTableChunk, contextHeader);
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

    private static string? ExtractConciseTableIntro(string text)
    {
        var sentences = text.Split(['.', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var s in sentences)
        {
            if (s.Contains("таблиц", StringComparison.OrdinalIgnoreCase) && s.Length <= 120)
            {
                return s;
            }
        }
        return null;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DocumentChunk> ChunkAsync(
        Document document,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Fallback for raw Document: split paragraphs into structural blocks
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
            else if (NumberedHeadingRegex.IsMatch(p) && p.Length < 100)
            {
                blocks.Add(new HeadingBlock(FormattableString.Invariant($"{document.Metadata.Id}#b{i}"), p, p.Contains('.') ? 2 : 1, prov));
            }
            else if (p.StartsWith('|') && p.Contains('\n') && TryParseMarkdownTable(p, FormattableString.Invariant($"{document.Metadata.Id}#t{i}"), prov, out var tableBlock))
            {
                blocks.Add(tableBlock);
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

    private static bool TryParseMarkdownTable(string text, string blockId, BlockProvenance prov, out TableBlock tableBlock)
    {
        tableBlock = null!;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2)
        {
            return false;
        }

        // Header line
        var headerCols = lines[0].Split('|', StringSplitOptions.TrimEntries)
            .Where(c => !string.IsNullOrEmpty(c))
            .ToList();
        if (headerCols.Count == 0)
        {
            return false;
        }

        // Skip separator line if present (| --- | --- |)
        var dataStart = 1;
        if (lines.Length > 1 && lines[1].Contains("---"))
        {
            dataStart = 2;
        }

        var rows = new List<ExtractedTableRow>();
        for (var r = dataStart; r < lines.Length; r++)
        {
            var cells = lines[r].Split('|', StringSplitOptions.TrimEntries)
                .Where(c => !string.IsNullOrEmpty(c))
                .ToList();
            if (cells.Count == 0)
            {
                continue;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < Math.Min(headerCols.Count, cells.Count); c++)
            {
                map[headerCols[c]] = cells[c];
            }

            rows.Add(new ExtractedTableRow(rows.Count, cells, map));
        }

        if (rows.Count == 0)
        {
            return false;
        }

        var table = new ExtractedTable(blockId, string.Empty, headerCols, rows, prov.PageNumber ?? 1, prov.PageNumber ?? 1);
        tableBlock = new TableBlock(blockId, table, prov);
        return true;
    }
}

