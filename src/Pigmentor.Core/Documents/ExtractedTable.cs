using System.Globalization;
using System.Text;

namespace Pigmentor.Core.Documents;

/// <summary>
/// A single row within an extracted table preserving column-header-to-cell associations.
/// </summary>
public sealed record ExtractedTableRow(
    int RowIndex,
    IReadOnlyList<string> Values,
    IReadOnlyDictionary<string, string> CellsByHeader);

/// <summary>
/// First-class structural representation of a table extracted from PDF, Word, or Excel.
/// </summary>
public sealed record ExtractedTable(
    string TableId,
    string Caption,
    IReadOnlyList<string> Columns,
    IReadOnlyList<ExtractedTableRow> Rows,
    int PageStart,
    int PageEnd,
    string? SheetName = null,
    string? CellRange = null)
{
    /// <summary>
    /// Renders a deterministic markdown table representation with metadata header for embeddings and retrieval.
    /// </summary>
    public string ToMarkdownTable()
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(Caption))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Таблица: {Caption}");
        }

        if (PageStart > 0)
        {
            if (PageStart == PageEnd || PageEnd <= 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"Страница: {PageStart}");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"Страницы: {PageStart}–{PageEnd}");
            }
        }

        if (!string.IsNullOrWhiteSpace(SheetName))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Лист: {SheetName}");
        }

        if (Columns.Count > 0)
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            sb.Append("| ").Append(string.Join(" | ", Columns)).AppendLine(" |");
            sb.Append("| ").Append(string.Join(" | ", Columns.Select(_ => "---"))).AppendLine(" |");
        }

        foreach (var row in Rows)
        {
            sb.Append("| ").Append(string.Join(" | ", row.Values)).AppendLine(" |");
        }

        return sb.ToString().TrimEnd();
    }
}

