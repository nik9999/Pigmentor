using System.Globalization;
using ExcelDataReader;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Extraction;

/// <summary>
/// Extracts first-class structured tables and provenance from Excel (<c>.xlsx</c>) workbooks.
/// </summary>
public sealed class XlsxFileExtractor : IFileExtractor
{
    private readonly ExtractionLimits _limits;

    static XlsxFileExtractor()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public XlsxFileExtractor(ExtractionLimits? limits = null)
    {
        _limits = limits ?? ExtractionLimits.Default;
    }

    public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".xlsx"
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

        using var reader = ExcelReaderFactory.CreateOpenXmlReader(stream);

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sheetName = reader.Name ?? "Sheet";

            var rowsData = new List<List<string>>();
            var maxCols = 0;

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fieldCount = reader.FieldCount;
                var rowCells = new List<string>(fieldCount);
                var rowHasContent = false;

                for (var col = 0; col < fieldCount; col++)
                {
                    var val = reader.GetValue(col);
                    var str = val?.ToString()?.Trim() ?? string.Empty;
                    if (!string.IsNullOrEmpty(str))
                    {
                        rowHasContent = true;
                    }
                    rowCells.Add(str);
                }

                if (rowHasContent)
                {
                    rowsData.Add(rowCells);
                    if (rowCells.Count > maxCols)
                    {
                        maxCols = rowCells.Count;
                    }
                }

                if (rowsData.Count >= _limits.MaxTableRows)
                {
                    break;
                }
            }

            if (rowsData.Count == 0)
            {
                continue;
            }

            // Row 0 is column headers
            var headerRow = rowsData[0];
            var columns = new List<string>(maxCols);
            for (var c = 0; c < maxCols; c++)
            {
                var h = c < headerRow.Count ? headerRow[c] : string.Empty;
                if (string.IsNullOrWhiteSpace(h))
                {
                    h = FormattableString.Invariant($"Колонка {c + 1}");
                }
                columns.Add(h);
            }

            var tableRows = new List<ExtractedTableRow>(rowsData.Count - 1);
            for (var r = 1; r < rowsData.Count; r++)
            {
                var rData = rowsData[r];
                var cellValues = new List<string>(maxCols);
                var cellsByHeader = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                for (var c = 0; c < maxCols; c++)
                {
                    var cellVal = c < rData.Count ? rData[c] : string.Empty;
                    cellValues.Add(cellVal);
                    var colHeader = columns[c];
                    cellsByHeader[colHeader] = cellVal;
                }

                tableRows.Add(new ExtractedTableRow(r + 1, cellValues, cellsByHeader));
            }

            var endColLetter = ToColumnLetter(maxCols);
            var cellRange = FormattableString.Invariant($"A1:{endColLetter}{rowsData.Count}");
            var tableId = FormattableString.Invariant($"{sheetName}-table");
            var tableCaption = FormattableString.Invariant($"Лист: {sheetName}");

            var extractedTable = new ExtractedTable(
                TableId: tableId,
                Caption: tableCaption,
                Columns: columns,
                Rows: tableRows,
                PageStart: 1,
                PageEnd: 1,
                SheetName: sheetName,
                CellRange: cellRange);

            var provenance = new BlockProvenance(
                DocumentId: metadata.Id,
                PageNumber: 1,
                SheetName: sheetName,
                CellRange: cellRange,
                TableId: tableId,
                TableCaption: tableCaption,
                RowStart: 2,
                RowEnd: rowsData.Count);

            blocks.Add(new TableBlock(
                BlockId: FormattableString.Invariant($"{metadata.Id}#b{blockIndex++}"),
                Table: extractedTable,
                Provenance: provenance));

        } while (reader.NextResult());

        return Task.FromResult(new ExtractedDocument(metadata.Id, filePath, ".xlsx", blocks, metadata));
    }

    private static string ToColumnLetter(int columnNumber)
    {
        if (columnNumber <= 0)
        {
            return "A";
        }

        var dividend = columnNumber;
        var columnName = string.Empty;

        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            columnName = Convert.ToChar(65 + modulo) + columnName;
            dividend = (dividend - modulo) / 26;
        }

        return columnName;
    }
}
