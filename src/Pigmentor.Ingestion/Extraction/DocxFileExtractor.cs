using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Extraction;

/// <summary>
/// Extracts structural headings, paragraphs, and tables from Word (<c>.docx</c>) documents.
/// </summary>
public sealed class DocxFileExtractor : IFileExtractor
{
    private readonly ExtractionLimits _limits;

    public DocxFileExtractor(ExtractionLimits? limits = null)
    {
        _limits = limits ?? ExtractionLimits.Default;
    }

    public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".docx"
    };

    public Task<ExtractedDocument> ExtractAsync(
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
        var blockIndex = 0;
        var tableCounter = 0;
        string? currentHeading = null;

        using var doc = WordprocessingDocument.Open(stream, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return Task.FromResult(new ExtractedDocument(metadata.Id, filePath, ".docx", blocks, metadata));
        }

        foreach (var element in body.ChildElements)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (element is Paragraph p)
            {
                var text = p.InnerText?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var styleId = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                var (isHeading, level) = DetectHeading(styleId, text);

                if (isHeading)
                {
                    currentHeading = text;
                    var prov = new BlockProvenance(metadata.Id, PageNumber: 1, Heading: text);
                    blocks.Add(new HeadingBlock(
                        BlockId: FormattableString.Invariant($"{metadata.Id}#b{blockIndex++}"),
                        Text: text,
                        Level: level,
                        Provenance: prov));
                }
                else
                {
                    var prov = new BlockProvenance(metadata.Id, PageNumber: 1, Heading: currentHeading);
                    blocks.Add(new TextBlock(
                        BlockId: FormattableString.Invariant($"{metadata.Id}#b{blockIndex++}"),
                        Text: text,
                        Provenance: prov));
                }
            }
            else if (element is Table t)
            {
                tableCounter++;
                var tableBlock = ExtractWordTable(t, metadata.Id, tableCounter, ref blockIndex, currentHeading);
                if (tableBlock != null)
                {
                    blocks.Add(tableBlock);
                }
            }
        }

        return Task.FromResult(new ExtractedDocument(metadata.Id, filePath, ".docx", blocks, metadata));
    }

    private static (bool IsHeading, int Level) DetectHeading(string? styleId, string text)
    {
        if (!string.IsNullOrEmpty(styleId))
        {
            var lower = styleId.ToLowerInvariant();
            if (lower.Contains("heading1") || lower.Contains("заголовок1"))
            {
                return (true, 1);
            }
            if (lower.Contains("heading2") || lower.Contains("заголовок2"))
            {
                return (true, 2);
            }
            if (lower.Contains("heading3") || lower.Contains("заголовок3"))
            {
                return (true, 3);
            }
            if (lower.Contains("heading") || lower.Contains("заголовок") || lower.Contains("title"))
            {
                return (true, 1);
            }
        }

        if (text.StartsWith("# ", StringComparison.Ordinal))
        {
            return (true, 1);
        }
        if (text.StartsWith("## ", StringComparison.Ordinal))
        {
            return (true, 2);
        }
        if (text.StartsWith("### ", StringComparison.Ordinal))
        {
            return (true, 3);
        }

        return (false, 0);
    }

    private static TableBlock? ExtractWordTable(
        Table table,
        string documentId,
        int tableNumber,
        ref int blockIndex,
        string? currentHeading)
    {
        var xmlRows = table.Elements<TableRow>().ToList();
        if (xmlRows.Count == 0)
        {
            return null;
        }

        // Header row
        var headerCells = xmlRows[0].Elements<TableCell>().Select(c => c.InnerText?.Trim() ?? string.Empty).ToList();
        var columns = new List<string>(headerCells.Count);
        for (var c = 0; c < headerCells.Count; c++)
        {
            var h = headerCells[c];
            if (string.IsNullOrWhiteSpace(h))
            {
                h = FormattableString.Invariant($"Колонка {c + 1}");
            }
            columns.Add(h);
        }

        var tableRows = new List<ExtractedTableRow>(xmlRows.Count - 1);
        for (var r = 1; r < xmlRows.Count; r++)
        {
            var cells = xmlRows[r].Elements<TableCell>().Select(c => c.InnerText?.Trim() ?? string.Empty).ToList();
            var rowValues = new List<string>(columns.Count);
            var cellsByHeader = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var c = 0; c < columns.Count; c++)
            {
                var val = c < cells.Count ? cells[c] : string.Empty;
                rowValues.Add(val);
                cellsByHeader[columns[c]] = val;
            }

            tableRows.Add(new ExtractedTableRow(r + 1, rowValues, cellsByHeader));
        }

        var tableId = FormattableString.Invariant($"table-{tableNumber}");
        var caption = FormattableString.Invariant($"Таблица {tableNumber}");

        var extractedTable = new ExtractedTable(
            TableId: tableId,
            Caption: caption,
            Columns: columns,
            Rows: tableRows,
            PageStart: 1,
            PageEnd: 1);

        var prov = new BlockProvenance(
            DocumentId: documentId,
            PageNumber: 1,
            Heading: currentHeading,
            TableId: tableId,
            TableCaption: caption,
            RowStart: 2,
            RowEnd: xmlRows.Count);

        return new TableBlock(
            BlockId: FormattableString.Invariant($"{documentId}#b{blockIndex++}"),
            Table: extractedTable,
            Provenance: prov);
    }
}

