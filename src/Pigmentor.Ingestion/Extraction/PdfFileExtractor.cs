using System.Globalization;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Ingestion.Normalization.Rules;
using Pigmentor.Ingestion.Quality;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Pigmentor.Ingestion.Extraction;

/// <summary>
/// Layout-aware PDF extractor that identifies structural headings, paragraphs,
/// first-class tables (including multi-page continuations), embedded figures with surrounding context,
/// and scanned pages/tables.
/// </summary>
public sealed class PdfFileExtractor : IFileExtractor
{
    private readonly IOcrEngine? _ocrEngine;
    private readonly IOcrTableDetector _ocrTableDetector;
    private readonly ExtractionLimits _limits;

    public PdfFileExtractor(
        IOcrEngine? ocrEngine = null,
        IOcrTableDetector? ocrTableDetector = null,
        ExtractionLimits? limits = null)
    {
        _ocrEngine = ocrEngine;
        _ocrTableDetector = ocrTableDetector ?? new OcrTableDetector();
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

            // 1. Check for scanned page (Prompt §14, §22)
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

                // Multi-page table continuation check (Prompt §23, §24, §25)
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

            // 5. Extract non-decorative figures with surrounding context or route embedded tables (Prompt §2-§6, §15)
            await ExtractFiguresAsync(page, metadata.Id, counters, blocks, nonTableLines, cancellationToken).ConfigureAwait(false);
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
                counters.ScannedPagesProcessed++;

                if (_ocrEngine != null && TryGetImageBytes(image, out var bytes))
                {
                    using var ms = new MemoryStream(bytes);
                    var ocrResult = await _ocrEngine.RecognizeAsync(ms, cancellationToken).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(ocrResult?.Text))
                    {
                        var assessment = OcrQualityGate.Assess(ocrResult.Text);
                        if (assessment.IsAcceptable)
                        {
                            var caption = FormattableString.Invariant($"Таблица. Страница {page.Number}");
                            if (_ocrTableDetector.TryDetectTable(ocrResult, documentId, page.Number, caption, out var scannedTable, cancellationToken))
                            {
                                counters.TableIndex++;
                                counters.OcrTablesDetected++;
                                var prov = new BlockProvenance(
                                    DocumentId: documentId,
                                    PageNumber: page.Number,
                                    PageStart: page.Number,
                                    PageEnd: page.Number,
                                    TableId: scannedTable.TableId,
                                    TableCaption: scannedTable.Caption,
                                    RowStart: 2,
                                    RowEnd: scannedTable.Rows.Count + 1);

                                blocks.Add(new TableBlock(
                                    BlockId: FormattableString.Invariant($"{documentId}#b{counters.BlockIndex++}"),
                                    Table: scannedTable,
                                    Provenance: prov));
                            }
                            else
                            {
                                counters.OcrTablesRejected++;
                                var prov = new BlockProvenance(documentId, PageNumber: page.Number);
                                blocks.Add(new TextBlock(
                                    BlockId: FormattableString.Invariant($"{documentId}#b{counters.BlockIndex++}"),
                                    Text: ocrResult.Text.Trim(),
                                    Provenance: prov));
                            }
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
        List<TextLineDto> nonTableLines,
        CancellationToken cancellationToken)
    {
        foreach (var img in page.GetImages())
        {
            cancellationToken.ThrowIfCancellationRequested();

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

            // Look for figure caption near image
            var caption = FindNearbyCaption(page, img.BoundingBox, "рисунок", "рис.", "figure", "fig.");
            if (string.IsNullOrWhiteSpace(caption))
            {
                caption = FormattableString.Invariant($"Рисунок {counters.FigureIndex + 1}. Страница {page.Number}");
            }

            var ocrText = string.Empty;
            if (_ocrEngine != null && TryGetImageBytes(img, out var bytes))
            {
                using var ms = new MemoryStream(bytes);
                var ocrResult = await _ocrEngine.RecognizeAsync(ms, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(ocrResult?.Text))
                {
                    var assessment = OcrQualityGate.Assess(ocrResult.Text);
                    if (assessment.IsAcceptable)
                    {
                        // Check if embedded image is actually a structured table (§15)
                        if (_ocrTableDetector.TryDetectTable(ocrResult, documentId, page.Number, caption, out var detectedTable, cancellationToken))
                        {
                            counters.TableIndex++;
                            counters.OcrTablesDetected++;
                            var tableProv = new BlockProvenance(
                                DocumentId: documentId,
                                PageNumber: page.Number,
                                PageStart: page.Number,
                                PageEnd: page.Number,
                                TableId: detectedTable.TableId,
                                TableCaption: detectedTable.Caption,
                                RowStart: 2,
                                RowEnd: detectedTable.Rows.Count + 1,
                                AssetReference: FormattableString.Invariant($"img-p{page.Number}-{counters.FigureIndex + 1}.png"));

                            blocks.Add(new TableBlock(
                                BlockId: FormattableString.Invariant($"{documentId}#b{counters.BlockIndex++}"),
                                Table: detectedTable,
                                Provenance: tableProv));

                            // Embedded image table routed to TableBlock, do NOT create FigureBlock (§15, §35)
                            continue;
                        }

                        ocrText = ocrResult.Text.Trim();
                    }
                    else
                    {
                        counters.FigureOcrRejected++;
                    }
                }
            }

            counters.FigureIndex++;
            var figureId = FormattableString.Invariant($"Рисунок {counters.FigureIndex}");

            // Extract bounded surrounding context around the figure (§2-§6)
            var surroundingContext = ExtractSurroundingContext(
                nonTableLines,
                img.BoundingBox,
                caption,
                page.Height,
                _limits.MaxFigureContextChars);

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
                SurroundingContext: surroundingContext,
                AssetReference: prov.AssetReference,
                Provenance: prov));
        }
    }

    private static string ExtractSurroundingContext(
        List<TextLineDto> nonTableLines,
        UglyToad.PdfPig.Core.PdfRectangle imageBounds,
        string caption,
        double pageHeight,
        int maxContextChars)
    {
        var linesAbove = new List<TextLineDto>();
        var linesBelow = new List<TextLineDto>();

        foreach (var line in nonTableLines)
        {
            // Exclude lines matching caption
            if (IsCaptionLine(line.Text, caption))
            {
                continue;
            }

            // Exclude page numbers and layout noise
            if (PageNumberNoiseRule.IsPageNumberNoise(line.Text))
            {
                continue;
            }

            // Exclude decorative short lines / symbols
            if (line.Text.Trim().Length < 3)
            {
                continue;
            }

            // Exclude extreme running header / footer lines (top 4% or bottom 4% of page)
            if (line.Bottom > pageHeight * 0.96 || line.Top < pageHeight * 0.04)
            {
                continue;
            }

            // In PdfPig, Bottom is distance from bottom of page. Higher Bottom => higher on page (above image).
            if (line.Bottom >= imageBounds.Bottom)
            {
                linesAbove.Add(line);
            }
            else
            {
                linesBelow.Add(line);
            }
        }

        // Nearest meaningful paragraph BEFORE the figure:
        // linesAbove is sorted descending by Y (top of page to bottom of page).
        // The line immediately before the image is linesAbove[^1].
        var beforeLines = new List<TextLineDto>();
        for (var i = linesAbove.Count - 1; i >= 0; i--)
        {
            var line = linesAbove[i];
            if (beforeLines.Count > 0)
            {
                var prevLine = beforeLines[0];
                var gap = line.Bottom - prevLine.Top;
                if (gap > line.Height * 1.8)
                {
                    break;
                }
            }

            beforeLines.Insert(0, line);
        }

        // Nearest meaningful paragraph AFTER the figure:
        // linesBelow is sorted descending by Y.
        // The line immediately after the image is linesBelow[0].
        var afterLines = new List<TextLineDto>();
        for (var i = 0; i < linesBelow.Count; i++)
        {
            var line = linesBelow[i];
            if (afterLines.Count > 0)
            {
                var prevLine = afterLines[^1];
                var gap = prevLine.Bottom - line.Top;
                if (gap > prevLine.Height * 1.8)
                {
                    break;
                }
            }

            afterLines.Add(line);
        }

        var beforeText = string.Join(" ", beforeLines.Select(l => l.Text.Trim())).Trim();
        var afterText = string.Join(" ", afterLines.Select(l => l.Text.Trim())).Trim();

        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(beforeText))
        {
            parts.Add(beforeText);
        }

        if (!string.IsNullOrWhiteSpace(afterText))
        {
            parts.Add(afterText);
        }

        if (parts.Count == 0)
        {
            return string.Empty;
        }

        var combined = string.Join("\n", parts);
        if (combined.Length <= maxContextChars)
        {
            return combined;
        }

        // Safely truncate at word boundary
        var truncated = combined[..maxContextChars];
        var lastSpace = truncated.LastIndexOf(' ');
        if (lastSpace > maxContextChars / 2)
        {
            truncated = truncated[..lastSpace];
        }

        return truncated.Trim();
    }

    private static bool IsCaptionLine(string lineText, string caption)
    {
        var lt = lineText.Trim();
        var ct = caption.Trim();
        if (string.Equals(lt, ct, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ct.StartsWith(lt, StringComparison.OrdinalIgnoreCase) && lt.Length > 8)
        {
            return true;
        }

        var lower = lt.ToLowerInvariant();
        return lower.StartsWith("рисунок", StringComparison.Ordinal)
            || lower.StartsWith("рис.", StringComparison.Ordinal)
            || lower.StartsWith("figure", StringComparison.Ordinal)
            || lower.StartsWith("fig.", StringComparison.Ordinal);
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
        // Conservative multi-page merge criteria (§23, §24):
        // 1. Strictly consecutive pages
        if (nextTable.PageStart != prevTable.PageEnd + 1)
        {
            return false;
        }

        // 2. Same column count
        if (prevTable.Columns.Count != nextTable.Columns.Count || prevTable.Columns.Count == 0)
        {
            return false;
        }

        var isContinuationCaption = nextTable.Caption.Contains("продолжение", StringComparison.OrdinalIgnoreCase)
            || nextTable.Caption.Contains("continued", StringComparison.OrdinalIgnoreCase);

        // 3. If next table has a caption indicating an independent new table (e.g., "Таблица 2" vs "Таблица 1"), reject
        if (!isContinuationCaption &&
            !string.IsNullOrWhiteSpace(nextTable.Caption) &&
            !string.Equals(prevTable.Caption, nextTable.Caption, StringComparison.OrdinalIgnoreCase) &&
            (nextTable.Caption.StartsWith("таблица", StringComparison.OrdinalIgnoreCase) || nextTable.Caption.StartsWith("table", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var matchingHeaders = prevTable.Columns.Zip(nextTable.Columns)
            .Count(p => string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase));

        var headerMatchRatio = (double)matchingHeaders / prevTable.Columns.Count;

        return isContinuationCaption || headerMatchRatio >= 0.75;
    }

    private static ExtractedTable MergeTables(ExtractedTable prev, ExtractedTable next, int pageNum)
    {
        var combinedRows = new List<ExtractedTableRow>(prev.Rows.Count + next.Rows.Count + 1);
        combinedRows.AddRange(prev.Rows);

        var nextColumnsMatchHeader = prev.Columns.Zip(next.Columns)
            .Count(p => string.Equals(p.First.Trim(), p.Second.Trim(), StringComparison.OrdinalIgnoreCase)) >= Math.Max(1, (int)(prev.Columns.Count * 0.75));

        var cleanNextRows = new List<ExtractedTableRow>();

        // If next page header was not repeated and next.Columns is actually a continuation data row (§25)
        if (!nextColumnsMatchHeader && next.Columns.Count == prev.Columns.Count)
        {
            var cellsByHeader = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < prev.Columns.Count; c++)
            {
                cellsByHeader[prev.Columns[c]] = next.Columns[c];
            }
            cleanNextRows.Add(new ExtractedTableRow(1, next.Columns, cellsByHeader));
        }

        // Append rows skipping any row that repeats the table header (§25: repeated header not treated as data row)
        foreach (var row in next.Rows)
        {
            if (!IsRepeatedHeaderRow(row, prev.Columns))
            {
                cleanNextRows.Add(row);
            }
        }

        var offset = prev.Rows.Count;
        for (var i = 0; i < cleanNextRows.Count; i++)
        {
            combinedRows.Add(cleanNextRows[i] with { RowIndex = offset + i + 1 });
        }

        return prev with
        {
            Rows = combinedRows,
            PageEnd = pageNum
        };
    }

    private static bool IsRepeatedHeaderRow(ExtractedTableRow row, IReadOnlyList<string> headers)
    {
        if (row.Values.Count != headers.Count)
        {
            return false;
        }

        var matching = row.Values.Zip(headers)
            .Count(p => string.Equals(p.First.Trim(), p.Second.Trim(), StringComparison.OrdinalIgnoreCase));

        return (double)matching / headers.Count >= 0.75;
    }

    private static bool TryGetImageBytes(IPdfImage image, out byte[] bytes)
    {
        if (image.TryGetPng(out var pngBytes) && pngBytes != null && pngBytes.Length > 0)
        {
            bytes = pngBytes;
            return true;
        }

        if (!image.RawBytes.IsEmpty)
        {
            bytes = image.RawBytes.ToArray();
            return true;
        }

        bytes = [];
        return false;
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
        public int ScannedPagesProcessed;
        public int OcrTablesDetected;
        public int OcrTablesRejected;
        public int FigureOcrRejected;
    }
}
