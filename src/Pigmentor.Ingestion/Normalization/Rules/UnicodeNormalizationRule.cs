using System.Text;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Normalization.Rules;

/// <summary>
/// Normalizes Unicode text into canonical FormC, cleans zero-width/control characters and soft-hyphens,
/// converts non-breaking spaces, and strictly preserves engineering technical symbols (° Ω µ ± ≤ ≥ etc.).
/// </summary>
public static class UnicodeNormalizationRule
{
    private static readonly HashSet<char> ZeroWidthChars =
    [
        '\u200B', // Zero width space
        '\u200C', // Zero width non-joiner
        '\u200D', // Zero width joiner
        '\uFEFF'  // Zero width no-break space (BOM)
    ];

    public static ExtractedBlock Normalize(ExtractedBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        if (block is TableBlock tableBlock)
        {
            return NormalizeTableBlock(tableBlock);
        }

        var normalizedText = CleanText(block.Text);
        return block switch
        {
            TextBlock tb => tb with { Text = normalizedText },
            HeadingBlock hb => hb with { Text = normalizedText },
            FigureBlock fb => fb with
            {
                Caption = CleanText(fb.Caption),
                OcrText = CleanText(fb.OcrText),
                SurroundingContext = CleanText(fb.SurroundingContext)
            },
            CodeBlock cb => cb with { Text = normalizedText },
            ListBlock lb => lb with { Text = normalizedText },
            _ => block
        };
    }

    public static string CleanText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // 1. Canonical Unicode FormC
        var formC = text.Normalize(NormalizationForm.FormC);

        var sb = new StringBuilder(formC.Length);
        foreach (var ch in formC)
        {
            // Drop zero-width characters
            if (ZeroWidthChars.Contains(ch))
            {
                continue;
            }

            // Drop soft hyphen
            if (ch == '\u00AD')
            {
                continue;
            }

            // Convert non-breaking space to regular space
            if (ch == '\u00A0' || ch == '\u202F')
            {
                sb.Append(' ');
                continue;
            }

            // Drop non-printable control characters, but preserve standard whitespace (\t, \n, \r)
            if (char.IsControl(ch))
            {
                if (ch == '\t' || ch == '\n' || ch == '\r')
                {
                    sb.Append(ch);
                }
                continue;
            }

            // Engineering / Technical symbols must be preserved
            // (° Ω µ ± ≤ ≥ × → ² ³ ‰ ≠ ≈ α β etc.)
            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static TableBlock NormalizeTableBlock(TableBlock tableBlock)
    {
        var table = tableBlock.Table;
        var normalizedCaption = CleanText(table.Caption);
        var normalizedColumns = table.Columns.Select(CleanText).ToList();

        var normalizedRows = new List<ExtractedTableRow>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            var cleanValues = row.Values.Select(CleanText).ToList();
            var cleanCells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < normalizedColumns.Count; i++)
            {
                var col = normalizedColumns[i];
                var val = i < cleanValues.Count ? cleanValues[i] : string.Empty;
                cleanCells[col] = val;
            }

            normalizedRows.Add(new ExtractedTableRow(row.RowIndex, cleanValues, cleanCells));
        }

        var cleanTable = table with
        {
            Caption = normalizedCaption,
            Columns = normalizedColumns,
            Rows = normalizedRows
        };

        return tableBlock with { Table = cleanTable };
    }
}

