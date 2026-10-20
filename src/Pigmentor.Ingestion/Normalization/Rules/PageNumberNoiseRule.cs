using System.Text.RegularExpressions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Normalization.Rules;

/// <summary>
/// Detects and removes standalone layout page number markers ("42", "Page 42 of 120", "Стр. 42 из 120")
/// from searchable text while ensuring the true page number remains intact in <see cref="BlockProvenance"/>.
/// </summary>
public static partial class PageNumberNoiseRule
{
    [GeneratedRegex(@"^\s*(?:(?:page|стр\.?|страница)\s*)?\d+\s*(?:(?:of|из)\s*\d+)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex StandalonePageNumberRegex();

    [GeneratedRegex(@"^\s*-\s*\d+\s*-\s*$")]
    private static partial Regex HyphenatedPageNumberRegex();

    public static ExtractedBlock Normalize(ExtractedBlock block, NormalizationDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (block is TableBlock or CodeBlock)
        {
            return block;
        }

        var text = block.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return block;
        }

        var lines = text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();

        var modified = false;

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (IsPageNumberNoise(line))
            {
                lines.RemoveAt(i);
                diagnostics.RemovedPageNumbers++;
                modified = true;
            }
        }

        if (!modified)
        {
            return block;
        }

        var cleanedText = string.Join("\n", lines).Trim();
        return block switch
        {
            TextBlock tb => tb with { Text = cleanedText },
            HeadingBlock hb => hb with { Text = cleanedText },
            FigureBlock fb => fb with { SurroundingContext = cleanedText },
            _ => block
        };
    }

    public static bool IsPageNumberNoise(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > 30)
        {
            return false;
        }

        return StandalonePageNumberRegex().IsMatch(line) || HyphenatedPageNumberRegex().IsMatch(line);
    }
}

