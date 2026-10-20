using System.Globalization;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Ingestion.Quality;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Pigmentor.Ingestion.Extraction;

/// <summary>
/// Layout-aware PDF extractor that identifies structural headings, paragraphs,
/// first-class tables (including multi-page continuations), embedded figures, and scanned pages.
/// </summary>
public sealed class PdfFileExtractor : IFileExtractor
{
    private readonly IOcrEngine? _ocrEngine;
    private readonly ExtractionLimits _limits;

    public PdfFileExtractor(IOcrEngine? ocrEngine = null, ExtractionLimits? limits = null)
    {
        _ocrEngine = ocrEngine;
        _limits = limits ?? ExtractionLimits.Default;
    }

    public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf"
    };

    public async Task<ExtractedDocument> ExtractAsync(
        Stream stream,
        string filePath,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(metadata);

        FileSignatureValidator.Validate(stream, filePath, _limits);

        var blocks = new List<ExtractedBlock>();
        var counters = new ExtractionCounters();

        using var pdfDocument = PdfDocument.Open(stream);
        var pageCount = Math.Min(pdfDocument.NumberOfPages, _limits.MaxPages);

        TableBlock? pendingTableBlock = null;

        for (var pageNum = 1; pageNum <= pageCount; pageNum++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = pdfDocument.GetPage(pageNum);

            // 1. Check for scanned page (Prompt §22)
            if (await TryExtractScannedPageAsync(page, metadata.Id, counters, blocks, cancellationToken).ConfigureAwait(false))
            {
                pendingTableBlock = null;
                continue;
            }

            var words = page.GetWords().ToList();
            if (words.Count == 0 && !page.GetImages().Any())
            {
                // Empty page: normalizer will drop it
                continue;
            }

            // 2. Group words into horizontal lines
            var lines = GroupWordsIntoLines(words);
            var medianFontSize = ComputeMedianFontSize(words);

            // 3. Detect tables on the page
            var (tableRegions, nonTableLines) = DetectTableRegions(lines);

            foreach (var region in tableRegions)
            {
                counters.TableIndex++;
                var table = BuildTableFromLines(region.Lines, region.Caption, pageNum, counters.TableIndex);

                // Multi-page table continuation check (Prompt §12)
                if (pendingTableBlock != null && CanMergeMultiPageTable(pendingTableBlock.Table, table))
                {
                    var mergedTable = MergeTables(pendingTableBlock.Table, table, pageNum);
                    var mergedProv = pendingTableBlock.Provenance with
                    {
                        PageEnd = pageNum,
                        RowEnd = mergedTable.Rows.Count + 1
                    };

                    // Replace pending table block with merged version
                    var replaceIndex = blocks.FindIndex(b => b.BlockId == pendingTableBlock.BlockId);
                    if (replaceIndex >= 0)
                    {
                        pendingTableBlock = new TableBlock(pendingTableBlock.BlockId, mergedTable, mergedProv);
                        blocks[replaceIndex] = pendingTableBlock;
                    }
                }
                else
                {
                    var prov = new BlockProvenance(
                        DocumentId: metadata.Id,
                        PageNumber: pageNum,
                        PageStart: pageNum,
                        PageEnd: pageNum,
                        TableId: table.TableId,
                        TableCaption: table.Caption,
                        RowStart: 2,
                        RowEnd: table.Rows.Count + 1);

                    var tableBlock = new TableBlock(
                        BlockId: FormattableString.Invariant($"{metadata.Id}#b{counters.BlockIndex++}"),
                        Table: table,
                        Provenance: prov);

                    blocks.Add(tableBlock);
                    pendingTableBlock = tableBlock;
                }
            }

            if (tableRegions.Count == 0)
            {
                pendingTableBlock = null;
            }

            // 4. Extract headings and prose paragraphs from remaining lines
            string? currentHeading = null;
            var currentParagraphLines = new List<TextLineDto>();

            foreach (var line in nonTableLines)
            {
                if (IsHeadingLine(line, medianFontSize))
                {
                    // Flush accumulated paragraph
                    FlushParagraph(currentParagraphLines, metadata.Id, pageNum, currentHeading, counters, blocks);

                    currentHeading = line.Text;
                    var prov = new BlockProvenance(metadata.Id, PageNumber: pageNum, Heading: line.Text);
                    blocks.Add(new HeadingBlock(
                        BlockId: FormattableString.Invariant($"{metadata.Id}#b{counters.BlockIndex++}"),
                        Text: line.Text,
                        Level: line.AverageFontSize > medianFontSize * 1.5 ? 1 : 2,
                        Provenance: prov));
                }
                else
                {
                    // Check if vertical gap indicates a new paragraph
                    if (currentParagraphLines.Count > 0)
                    {
                        var prevLine = currentParagraphLines[^1];
                        var gap = prevLine.Bottom - line.Top;
                        if (gap > prevLine.Height * 1.6)
                        {
                            FlushParagraph(currentParagraphLines, metadata.Id, pageNum, currentHeading, counters, blocks);
                        }
                    }

                    currentParagraphLines.Add(line);
                }
            }

            FlushParagraph(currentParagraphLines, metadata.Id, pageNum, currentHeading, counters, blocks);

            // 5. Extract non-decorative figures (Prompt §18-§21)
            await ExtractFiguresAsync(page, metadata.Id, counters, blocks, cancellationToken).ConfigureAwait(false);
        }

        return new ExtractedDocument(metadata.Id, filePath, ".pdf", blocks, metadata);
    }

    private async Task<bool> TryExtractScannedPageAsync(
        Page page,
        string documentId,
        ExtractionCounters counters,
        List<ExtractedBlock> blocks,
        CancellationToken cancellationToken)
    {
        var images = page.GetImages().ToList();
        var wordCount = page.GetWords().Count();

        // Scanned page has very few or zero text words and a dominant raster image
        if (wordCount <= 3 && images.Count == 1)
        {
            var image = images[0];
            var pageArea = page.Width * page.Height;
            var imageArea = image.BoundingBox.Width * image.BoundingBox.Height;

            if (pageArea > 0 && (imageArea / pageArea) >= 0.45)
            {
                if (_ocrEngine != null && image.TryGetPng(out var bytes) && bytes != null)
                {
                    using var ms = new MemoryStream(bytes);
                    var ocrResult = await _ocrEngine.RecognizeAsync(ms, cancellationToken).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(ocrResult?.Text))
                    {
                        var assessment = OcrQualityGate.Assess(ocrResult.Text);
                        if (assessment.IsAcceptable)
                        {
                            var prov = new BlockProvenance(documentId, PageNumber: page.Number);
                            blocks.Add(new TextBlock(
                                BlockId: FormattableString.Invariant($"{documentId}#b{counters.BlockIndex++}"),
                                Text: ocrResult.Text.Trim(),
                                Provenance: prov));
                        }
                    }
                }

                // Page was handled via the scanned page OCR path
                return true;
            }
        }

        return false;
    }

    private async Task ExtractFiguresAsync(
        Page page,
        string documentId,
        ExtractionCounters counters,
        List<ExtractedBlock> blocks,
        CancellationToken cancellationToken)
    {
        foreach (var img in page.GetImages())
        {
            var w = img.BoundingBox.Width;
            var h = img.BoundingBox.Height;

            // Filter out decorative images (Prompt §21): tiny icons, thin dividers, logos
            if (w < 45 || h < 45)
            {
                continue;
            }

            var aspect = w / h;
            if (aspect > 9.0 || aspect < 0.11)
            {
                continue;
            }

            counters.FigureIndex++;
            var figureId = FormattableString.Invariant($"Рисунок {counters.FigureIndex}");

            // Look for figure caption near image
            var caption = FindNearbyCaption(page, img.BoundingBox, "рисунок", "рис.", "figure", "fig.");
            if (string.IsNullOrWhiteSpace(caption))
            {
                caption = FormattableString.Invariant($"Рисунок {counters.FigureIndex}. Страница {page.Number}");
            }

            var ocrText = string.Empty;
            if (_ocrEngine != null && img.TryGetPng(out var bytes) && bytes != null)
            {
                using var ms = new MemoryStream(bytes);
                var ocrResult = await _ocrEngine.RecognizeAsync(ms, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(ocrResult?.Text))
                {
                    var assessment = OcrQualityGate.Assess(ocrResult.Text);
                    if (assessment.IsAcceptable)
                    {
                        ocrText = ocrResult.Text.Trim();
                    }
                }
            }

            var prov = new BlockProvenance(
                DocumentId: documentId,
                PageNumber: page.Number,
                FigureId: figureId,
                AssetReference: FormattableString.Invariant($"img-p{page.Number}-{counters.FigureIndex}.png"));

            blocks.Add(new FigureBlock(
                BlockId: FormattableString.Invariant($"{documentId}#b{counters.BlockIndex++}"),
                FigureId: figureId,
                Caption: caption,
                OcrText: ocrText,
                SurroundingContext: string.Empty,
                AssetReference: prov.AssetReference,
                Provenance: prov));
        }
    }

    private static string FindNearbyCaption(Page page, UglyToad.PdfPig.Core.PdfRectangle bounds, params string[] prefixes)
    {
        var words = page.GetWords().ToList();
        var lines = GroupWordsIntoLines(words);

        // Find line just below or just above the image bounds
        foreach (var line in lines)
        {
            var isNear = Math.Abs(line.Top - bounds.Bottom) < 40 || Math.Abs(line.Bottom - bounds.Top) < 40;
            if (isNear)
            {
                var lower = line.Text.ToLowerInvariant();
                if (prefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal)))
                {
                    return line.Text;
                }
            }
        }

        return string.Empty;
    }

    private static void FlushParagraph(
        List<TextLineDto> lines,
        string documentId,
        int pageNumber,
        string? heading,
        ExtractionCounters counters,
        List<ExtractedBlock> blocks)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var text = string.Join(" ", lines.Select(l => l.Text.Trim())).Trim();
        lines.Clear();

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var prov = new BlockProvenance(documentId, PageNumber: pageNumber, Heading: heading);
        blocks.Add(new TextBlock(FormattableString.Invariant($"{documentId}#b{counters.BlockIndex++}"), text, prov));
    }

    private static bool IsHeadingLine(TextLineDto line, double medianFontSize)
    {
        if (line.AverageFontSize >= medianFontSize * 1.30 && line.Text.Length < 120)
        {
            return true;
        }

        var t = line.Text.Trim();
        if (t.StartsWith("# ", StringComparison.Ordinal) || t.StartsWith("## ", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static double ComputeMedianFontSize(List<Word> words)
    {
        if (words.Count == 0)
        {
            return 10.0;
        }

        var sizes = words.Select(w => (double)w.BoundingBox.Height).OrderBy(s => s).ToList();
        return sizes[sizes.Count / 2];
    }

    private static List<TextLineDto> GroupWordsIntoLines(List<Word> words)
    {
        if (words.Count == 0)
        {
            return [];
        }

        // Sort top-to-bottom (PdfPig Y coordinates go bottom-to-top, so higher Bottom means higher on page)
        var sorted = words.OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left).ToList();

        var lines = new List<TextLineDto>();
        var currentLineWords = new List<Word>();
        double? currentBaseline = null;

        foreach (var word in sorted)
        {
            var wordY = word.BoundingBox.Bottom;

            if (!currentBaseline.HasValue)
            {
                currentBaseline = wordY;
                currentLineWords.Add(word);
            }
            else if (Math.Abs(wordY - currentBaseline.Value) <= 3.5)
            {
                currentLineWords.Add(word);
            }
            else
            {
                lines.Add(CreateTextLine(currentLineWords));
                currentLineWords.Clear();
                currentBaseline = wordY;
                currentLineWords.Add(word);
            }
        }

        if (currentLineWords.Count > 0)
        {
            lines.Add(CreateTextLine(currentLineWords));
        }

        return lines;
    }

    private static TextLineDto CreateTextLine(List<Word> words)
    {
        var lineWords = words.OrderBy(w => w.BoundingBox.Left).ToList();
        var text = string.Join(" ", lineWords.Select(w => w.Text));
        var minLeft = lineWords.Min(w => w.BoundingBox.Left);
        var maxRight = lineWords.Max(w => w.BoundingBox.Right);
        var maxTop = lineWords.Max(w => w.BoundingBox.Top);
        var minBottom = lineWords.Min(w => w.BoundingBox.Bottom);
        var avgHeight = lineWords.Average(w => (double)w.BoundingBox.Height);

        return new TextLineDto(text, minLeft, maxRight, maxTop, minBottom, avgHeight, avgHeight, lineWords);
    }

    private static (List<TableRegionDto> TableRegions, List<TextLineDto> NonTableLines) DetectTableRegions(List<TextLineDto> lines)
    {
        var tableRegions = new List<TableRegionDto>();
        var nonTableLines = new List<TextLineDto>();

        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];

            // Check if line or subsequent lines look like a table row:
            // Either pipe-delimited or contains multiple distinct column positions (>= 3 words with significant gaps)
            if (IsPotentialTableRow(line))
            {
                var candidateTableLines = new List<TextLineDto> { line };
                var j = i + 1;

                while (j < lines.Count && IsPotentialTableRow(lines[j]))
                {
                    candidateTableLines.Add(lines[j]);
                    j++;
                }

                // If at least 2 consecutive lines form columns, treat as a table
                if (candidateTableLines.Count >= 2)
                {
                    string? caption = null;
                    if (nonTableLines.Count > 0)
                    {
                        var prev = nonTableLines[^1];
                        var lower = prev.Text.ToLowerInvariant();
                        if (lower.StartsWith("таблица", StringComparison.Ordinal) || lower.StartsWith("table", StringComparison.Ordinal))
                        {
                            caption = prev.Text;
                            nonTableLines.RemoveAt(nonTableLines.Count - 1);
                        }
                    }

                    tableRegions.Add(new TableRegionDto(candidateTableLines, caption));
                    i = j;
                    continue;
                }
            }

            nonTableLines.Add(line);
            i++;
        }

        return (tableRegions, nonTableLines);
    }

    private static bool IsPotentialTableRow(TextLineDto line)
    {
        // Explicit pipe-delimited
        if (line.Text.Contains('|') && line.Words.Count >= 2)
        {
            return true;
        }

        // Multi-column layout: line has >= 3 words with significant horizontal separation
        if (line.Words.Count >= 3)
        {
            var largeGaps = 0;
            for (var k = 0; k < line.Words.Count - 1; k++)
            {
                var gap = line.Words[k + 1].BoundingBox.Left - line.Words[k].BoundingBox.Right;
                if (gap > 18.0) // 18pt is well above normal space width (~3-5pt)
                {
                    largeGaps++;
                }
            }

            if (largeGaps >= 2)
            {
                return true;
            }
        }

        return false;
    }

    private static ExtractedTable BuildTableFromLines(List<TextLineDto> lines, string? caption, int pageNum, int tableIndex)
    {
        var tableId = FormattableString.Invariant($"table-{pageNum}-{tableIndex}");
        var finalCaption = !string.IsNullOrWhiteSpace(caption)
            ? caption
            : FormattableString.Invariant($"Таблица {tableIndex}");

        // If lines are pipe-delimited:
        if (lines.All(l => l.Text.Contains('|')))
        {
            var headerParts = lines[0].Text.Split('|', StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrEmpty(s)).ToList();

            var rows = new List<ExtractedTableRow>();
            for (var r = 1; r < lines.Count; r++)
            {
                var rowParts = lines[r].Text.Split('|', StringSplitOptions.TrimEntries)
                    .Where(s => !string.IsNullOrEmpty(s)).ToList();

                var cellsByHeader = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var c = 0; c < headerParts.Count; c++)
                {
                    var val = c < rowParts.Count ? rowParts[c] : string.Empty;
                    cellsByHeader[headerParts[c]] = val;
                }

                rows.Add(new ExtractedTableRow(r + 1, rowParts, cellsByHeader));
            }

            return new ExtractedTable(tableId, finalCaption, headerParts, rows, pageNum, pageNum);
        }

        // Determine columns from column gutters across all rows
        var columnIntervals = FindColumnIntervals(lines);
        var columns = new List<string>(columnIntervals.Count);

        // Header row: first line
        var headerCells = ExtractCellsByIntervals(lines[0], columnIntervals);
        for (var c = 0; c < columnIntervals.Count; c++)
        {
            var h = c < headerCells.Count ? headerCells[c] : string.Empty;
            if (string.IsNullOrWhiteSpace(h))
            {
                h = FormattableString.Invariant($"Колонка {c + 1}");
            }
            columns.Add(h);
        }

        var dataRows = new List<ExtractedTableRow>(lines.Count - 1);
        for (var r = 1; r < lines.Count; r++)
        {
            var cells = ExtractCellsByIntervals(lines[r], columnIntervals);
            var cellsByHeader = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var c = 0; c < columns.Count; c++)
            {
                var val = c < cells.Count ? cells[c] : string.Empty;
                cellsByHeader[columns[c]] = val;
            }

            dataRows.Add(new ExtractedTableRow(r + 1, cells, cellsByHeader));
        }

        return new ExtractedTable(tableId, finalCaption, columns, dataRows, pageNum, pageNum);
    }

    private static List<(double Left, double Right)> FindColumnIntervals(List<TextLineDto> lines)
    {
        // Cluster word start positions
        var allX = lines.SelectMany(l => l.Words).Select(w => w.BoundingBox.Left).OrderBy(x => x).ToList();
        if (allX.Count == 0)
        {
            return [(0, 1000)];
        }

        var clusters = new List<List<double>>();
        foreach (var x in allX)
        {
            if (clusters.Count == 0 || (x - clusters[^1].Average() > 25.0))
            {
                clusters.Add([x]);
            }
            else
            {
                clusters[^1].Add(x);
            }
        }

        var intervals = new List<(double Left, double Right)>();
        for (var i = 0; i < clusters.Count; i++)
        {
            var left = clusters[i].Min() - 5.0;
            var right = (i < clusters.Count - 1) ? clusters[i + 1].Min() - 5.0 : 10000.0;
            intervals.Add((left, right));
        }

        return intervals;
    }

    private static List<string> ExtractCellsByIntervals(TextLineDto line, List<(double Left, double Right)> intervals)
    {
        var cells = new List<string>(intervals.Count);

        foreach (var (left, right) in intervals)
        {
            var wordsInCol = line.Words
                .Where(w => w.BoundingBox.Left >= left && w.BoundingBox.Left < right)
                .OrderBy(w => w.BoundingBox.Left)
                .Select(w => w.Text);

            cells.Add(string.Join(" ", wordsInCol).Trim());
        }

        return cells;
    }

    private static bool CanMergeMultiPageTable(ExtractedTable prevTable, ExtractedTable nextTable)
    {
        // Conservative heuristic (Prompt §12):
        // 1. Same number of columns
        // 2. Either columns match or next table caption contains "продолжение" / "continued"
        if (prevTable.Columns.Count != nextTable.Columns.Count || prevTable.Columns.Count == 0)
        {
            return false;
        }

        var isContinuationCaption = nextTable.Caption.Contains("продолжение", StringComparison.OrdinalIgnoreCase)
            || nextTable.Caption.Contains("continued", StringComparison.OrdinalIgnoreCase);

        var matchingHeaders = prevTable.Columns.Zip(nextTable.Columns)
            .Count(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));

        var headerMatchRatio = (double)matchingHeaders / prevTable.Columns.Count;

        return isContinuationCaption || headerMatchRatio >= 0.75;
    }

    private static ExtractedTable MergeTables(ExtractedTable prev, ExtractedTable next, int pageNum)
    {
        var combinedRows = new List<ExtractedTableRow>(prev.Rows.Count + next.Rows.Count);
        combinedRows.AddRange(prev.Rows);

        var offset = prev.Rows.Count;
        foreach (var row in next.Rows)
        {
            combinedRows.Add(row with { RowIndex = offset + row.RowIndex });
        }

        return prev with
        {
            Rows = combinedRows,
            PageEnd = pageNum
        };
    }

    private sealed record TextLineDto(
        string Text,
        double Left,
        double Right,
        double Top,
        double Bottom,
        double Height,
        double AverageFontSize,
        IReadOnlyList<Word> Words);

    private sealed record TableRegionDto(
        List<TextLineDto> Lines,
        string? Caption);

    private sealed class ExtractionCounters
    {
        public int BlockIndex;
        public int TableIndex;
        public int FigureIndex;
    }
}
