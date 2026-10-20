namespace Pigmentor.Core.Documents;

/// <summary>
/// Fine-grained structural provenance tracking the exact origin of a block or chunk
/// back to the source document, page, table, sheet, range, or figure.
/// </summary>
public sealed record BlockProvenance(
    string DocumentId,
    int? PageNumber = null,
    int? PageStart = null,
    int? PageEnd = null,
    string? Section = null,
    string? Heading = null,
    string? TableId = null,
    string? TableCaption = null,
    int? RowStart = null,
    int? RowEnd = null,
    string? SheetName = null,
    string? CellRange = null,
    string? FigureId = null,
    string? AssetReference = null)
{
    /// <summary>
    /// Renders a human-readable citation label for this provenance entry.
    /// </summary>
    public string ToCitationReference()
    {
        var parts = new List<string>();

        if (PageNumber.HasValue)
        {
            parts.Add($"Стр. {PageNumber.Value}");
        }
        else if (PageStart.HasValue)
        {
            parts.Add(PageEnd.HasValue && PageEnd.Value != PageStart.Value
                ? $"Стр. {PageStart.Value}–{PageEnd.Value}"
                : $"Стр. {PageStart.Value}");
        }

        if (!string.IsNullOrWhiteSpace(SheetName))
        {
            parts.Add($"Лист: {SheetName}");
        }

        if (!string.IsNullOrWhiteSpace(CellRange))
        {
            parts.Add($"Диапазон: {CellRange}");
        }

        if (!string.IsNullOrWhiteSpace(TableCaption))
        {
            parts.Add($"Таблица: {TableCaption}");
        }
        else if (!string.IsNullOrWhiteSpace(TableId))
        {
            parts.Add($"Таблица {TableId}");
        }

        if (RowStart.HasValue)
        {
            parts.Add(RowEnd.HasValue && RowEnd.Value != RowStart.Value
                ? $"Строки {RowStart.Value}–{RowEnd.Value}"
                : $"Строка {RowStart.Value}");
        }

        if (!string.IsNullOrWhiteSpace(FigureId))
        {
            parts.Add($"Рисунок: {FigureId}");
        }

        if (!string.IsNullOrWhiteSpace(Heading))
        {
            parts.Add($"Раздел: {Heading}");
        }

        return string.Join(" | ", parts);
    }
}

