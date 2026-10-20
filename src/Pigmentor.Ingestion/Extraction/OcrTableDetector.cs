using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Ingestion.Quality;

namespace Pigmentor.Ingestion.Extraction;

/// <summary>
/// Conservative structural table detector for OCR results from scanned pages and embedded images.
/// Reconstructs tabular rows and column associations without relying on Vision models.
/// </summary>
public sealed class OcrTableDetector : IOcrTableDetector
{
    private const double MinConfidenceThreshold = 0.40;
    private const int MaxProseCellLength = 60;

    /// <inheritdoc />
    public bool TryDetectTable(
        OcrResult ocrResult,
        string documentId,
        int pageNumber,
        string? defaultCaption,
        [NotNullWhen(true)] out ExtractedTable? table,
        CancellationToken cancellationToken = default)
    {
        table = null;

        if (ocrResult == null || string.IsNullOrWhiteSpace(ocrResult.Text))
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 1. Strict quality gate assessment (§34: low-quality OCR must not become a table)
        var assessment = OcrQualityGate.Assess(ocrResult.Text);
        if (!assessment.IsAcceptable || ocrResult.Confidence < MinConfidenceThreshold)
        {
            return false;
        }

        // 2. Try geometric layout-based reconstruction if word bounding boxes are provided
        if (ocrResult.Lines != null && ocrResult.Lines.Count >= 2 &&
            TryDetectFromLayout(ocrResult.Lines, documentId, pageNumber, defaultCaption, out table, cancellationToken))
        {
            return true;
        }

        // 3. Try text-based reconstruction (pipe-delimited, tab-delimited, or multi-space aligned)
        var rawLines = ocrResult.Text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        if (rawLines.Count < 2)
        {
            return false;
        }

        return TryDetectFromTextLines(rawLines, documentId, pageNumber, defaultCaption, out table, cancellationToken);
    }

    private static bool TryDetectFromLayout(
        IReadOnlyList<OcrLine> lines,
        string documentId,
        int pageNumber,
        string? defaultCaption,
        [NotNullWhen(true)] out ExtractedTable? table,
        CancellationToken cancellationToken)
    {
        table = null;

        var validLines = lines
            .Where(l => l.Words != null && l.Words.Count > 0)
            .ToList();

        if (validLines.Count < 2)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Cluster X-coordinates to identify columns across lines
        var allX = validLines.SelectMany(l => l.Words!)
            .Select(w => w.Bounds.Left)
            .OrderBy(x => x)
            .ToList();

        if (allX.Count == 0)
        {
            return false;
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

        // Must have at least 2 distinct column intervals
        if (clusters.Count < 2)
        {
            return false;
        }

        var intervals = new List<(double Left, double Right)>();
        for (var i = 0; i < clusters.Count; i++)
        {
            var left = clusters[i].Min() - 5.0;
            var right = (i < clusters.Count - 1) ? clusters[i + 1].Min() - 5.0 : 10000.0;
            intervals.Add((left, right));
        }

        // Extract cells for each line
        var lineCells = new List<List<string>>();
        foreach (var line in validLines)
        {
            var row = new List<string>(intervals.Count);
            foreach (var (left, right) in intervals)
            {
                var wordsInCol = line.Words!
                    .Where(w => w.Bounds.Left >= left && w.Bounds.Left < right)
                    .OrderBy(w => w.Bounds.Left)
                    .Select(w => w.Text);

                row.Add(string.Join(" ", wordsInCol).Trim());
            }
            lineCells.Add(row);
        }

        // First row must have at least 2 non-empty column headers
        var headers = lineCells[0].Select((c, idx) => string.IsNullOrWhiteSpace(c) ? FormattableString.Invariant($"Колонка {idx + 1}") : c).ToList();
        if (headers.Count(h => !h.StartsWith("Колонка", StringComparison.Ordinal)) < 2)
        {
            return false;
        }

        var dataRows = new List<ExtractedTableRow>(lineCells.Count - 1);
        for (var r = 1; r < lineCells.Count; r++)
        {
            var cells = lineCells[r];
            var cellsByHeader = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < headers.Count; c++)
            {
                var val = c < cells.Count ? cells[c] : string.Empty;
                cellsByHeader[headers[c]] = val;
            }
            dataRows.Add(new ExtractedTableRow(r + 1, cells, cellsByHeader));
        }

        var tableId = FormattableString.Invariant($"ocr-table-{pageNumber}-1");
        var caption = !string.IsNullOrWhiteSpace(defaultCaption) ? defaultCaption : FormattableString.Invariant($"Таблица {pageNumber}");
        table = new ExtractedTable(tableId, caption, headers, dataRows, pageNumber, pageNumber);
        return true;
    }

    private static bool TryDetectFromTextLines(
        List<string> rawLines,
        string documentId,
        int pageNumber,
        string? defaultCaption,
        [NotNullWhen(true)] out ExtractedTable? table,
        CancellationToken cancellationToken)
    {
        table = null;

        string? caption = null;
        var startIdx = 0;

        // Check if first line is a table caption
        var firstLower = rawLines[0].ToLowerInvariant();
        if (firstLower.StartsWith("таблица", StringComparison.Ordinal) || firstLower.StartsWith("table", StringComparison.Ordinal))
        {
            caption = rawLines[0];
            startIdx = 1;
        }

        var candidateLines = rawLines.Skip(startIdx).ToList();
        if (candidateLines.Count < 2)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Case A: Pipe-delimited rows
        if (candidateLines.All(l => l.Contains('|')))
        {
            return TryBuildDelimitedTable(candidateLines, '|', documentId, pageNumber, caption ?? defaultCaption, out table);
        }

        // Case B: Tab-delimited rows
        if (candidateLines.All(l => l.Contains('\t')))
        {
            return TryBuildDelimitedTable(candidateLines, '\t', documentId, pageNumber, caption ?? defaultCaption, out table);
        }

        // Case C: Multi-space column separation (conservative!)
        if (TryBuildMultiSpaceTable(candidateLines, documentId, pageNumber, caption ?? defaultCaption, out table))
        {
            return true;
        }

        return false;
    }

    private static bool TryBuildDelimitedTable(
        List<string> lines,
        char delimiter,
        string documentId,
        int pageNumber,
        string? caption,
        [NotNullWhen(true)] out ExtractedTable? table)
    {
        table = null;

        var headerCells = lines[0]
            .Split(delimiter, StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        if (headerCells.Count < 2)
        {
            return false;
        }

        var dataRows = new List<ExtractedTableRow>(lines.Count - 1);
        for (var r = 1; r < lines.Count; r++)
        {
            var rowCells = lines[r]
                .Split(delimiter, StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToList();

            if (rowCells.Count == 0)
            {
                continue;
            }

            var cellsByHeader = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < headerCells.Count; c++)
            {
                var val = c < rowCells.Count ? rowCells[c] : string.Empty;
                cellsByHeader[headerCells[c]] = val;
            }

            dataRows.Add(new ExtractedTableRow(r + 1, rowCells, cellsByHeader));
        }

        if (dataRows.Count == 0)
        {
            return false;
        }

        var tableId = FormattableString.Invariant($"ocr-table-{pageNumber}-1");
        var finalCaption = !string.IsNullOrWhiteSpace(caption) ? caption : FormattableString.Invariant($"Таблица {pageNumber}");
        table = new ExtractedTable(tableId, finalCaption, headerCells, dataRows, pageNumber, pageNumber);
        return true;
    }

    private static bool TryBuildMultiSpaceTable(
        List<string> lines,
        string documentId,
        int pageNumber,
        string? caption,
        [NotNullWhen(true)] out ExtractedTable? table)
    {
        table = null;

        // Conservative checks to avoid false positives on normal prose:
        // 1. Every line must split into >= 2 columns using 2 or more consecutive spaces.
        // 2. All candidate rows must have the EXACT same column count.
        // 3. No candidate row should end with sentence punctuation ('.', '!', '?').
        // 4. No cell should exceed MaxProseCellLength characters.
        // 5. Must have at least 1 data row.

        var rowsSplits = new List<List<string>>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Ordinary prose lines typically end with period or question/exclamation mark
            if (trimmed.EndsWith('.') || trimmed.EndsWith('!') || trimmed.EndsWith('?'))
            {
                return false;
            }

            var parts = Regex.Split(trimmed, @"\s{2,}")
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();

            if (parts.Count < 2)
            {
                return false;
            }

            // Cell length check: tables have bounded concise cells, not narrative paragraphs
            if (parts.Any(p => p.Length > MaxProseCellLength))
            {
                return false;
            }

            rowsSplits.Add(parts);
        }

        // Check uniform column count
        var expectedColCount = rowsSplits[0].Count;
        if (expectedColCount < 2 || rowsSplits.Any(r => r.Count != expectedColCount))
        {
            return false;
        }

        var headers = rowsSplits[0];
        var dataRows = new List<ExtractedTableRow>(rowsSplits.Count - 1);

        for (var r = 1; r < rowsSplits.Count; r++)
        {
            var cells = rowsSplits[r];
            var cellsByHeader = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < headers.Count; c++)
            {
                var val = c < cells.Count ? cells[c] : string.Empty;
                cellsByHeader[headers[c]] = val;
            }
            dataRows.Add(new ExtractedTableRow(r + 1, cells, cellsByHeader));
        }

        if (dataRows.Count == 0)
        {
            return false;
        }

        var tableId = FormattableString.Invariant($"ocr-table-{pageNumber}-1");
        var finalCaption = !string.IsNullOrWhiteSpace(caption) ? caption : FormattableString.Invariant($"Таблица {pageNumber}");
        table = new ExtractedTable(tableId, finalCaption, headers, dataRows, pageNumber, pageNumber);
        return true;
    }
}

