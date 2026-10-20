using System.Text.RegularExpressions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Normalization.Rules;

/// <summary>
/// Reconstructs prose paragraphs from layout line breaks while preserving real paragraph boundaries
/// and exempting structured tables and preformatted code.
/// </summary>
public static partial class ParagraphReconstructionRule
{
    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex MultipleWhitespaceRegex();

    public static ExtractedBlock Normalize(ExtractedBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);

        // Prompt §32, §33: Tables and Code blocks are strictly exempt from prose line reconstruction
        if (block is TableBlock or CodeBlock)
        {
            return block;
        }

        if (block is TextBlock tb)
        {
            return tb with { Text = ReconstructProse(tb.Text) };
        }

        if (block is HeadingBlock hb)
        {
            return hb with { Text = CollapseSingleLine(hb.Text) };
        }

        if (block is FigureBlock fb)
        {
            return fb with
            {
                Caption = CollapseSingleLine(fb.Caption),
                SurroundingContext = ReconstructProse(fb.SurroundingContext)
            };
        }

        return block;
    }

    private static string ReconstructProse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalizedLineBreaks = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var paragraphs = normalizedLineBreaks.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        var cleanedParagraphs = new List<string>(paragraphs.Length);
        foreach (var p in paragraphs)
        {
            var trimmed = p.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            // Replace single newlines within paragraph with space
            var joined = trimmed.Replace('\n', ' ');
            var collapsed = MultipleWhitespaceRegex().Replace(joined, " ").Trim();
            if (!string.IsNullOrEmpty(collapsed))
            {
                cleanedParagraphs.Add(collapsed);
            }
        }

        return string.Join("\n\n", cleanedParagraphs);
    }

    private static string CollapseSingleLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var joined = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
        return MultipleWhitespaceRegex().Replace(joined, " ").Trim();
    }
}

