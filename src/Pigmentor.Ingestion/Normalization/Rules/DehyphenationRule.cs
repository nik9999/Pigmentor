using System.Text.RegularExpressions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Normalization.Rules;

/// <summary>
/// Conservative line-break hyphenation repair. Joins words split across layout lines
/// (e.g. "конфи-\nгурация" -> "конфигурация") while strictly preserving technical identifiers
/// and hyphenated compounds ("RS-485", "4-20 mA", "клиент-сервер", "Modbus-TCP" -> "Modbus TCP").
/// </summary>
public static partial class DehyphenationRule
{
    // Pattern 1: lowercase word split across line break or space (e.g. "конфи-\nгурация", "обслуживаю- щего", "незави- симых")
    [GeneratedRegex(@"(\p{Ll}+)-(?:\r?\n|\s+)(\p{Ll}+)")]
    private static partial Regex LowercaseHyphenSplitRegex();

    // Pattern 2: TitleCase word followed by uppercase acronym/model across line break (e.g. "Modbus-\nTCP" -> "Modbus TCP")
    [GeneratedRegex(@"(\p{Lu}\p{Ll}+)-\r?\n(\p{Lu}[\p{Lu}\p{Nd}]+)")]
    private static partial Regex TitleHyphenSplitRegex();

    // Pattern 3: Technical token hyphen spacing (e.g. "RS- 485" -> "RS-485", "AI- 8UI/D" -> "AI-8UI/D")
    [GeneratedRegex(@"\b([A-Za-z]{2,8})-(?:\r?\n|\s+)([0-9]+[A-Za-z0-9/]*)\b")]
    private static partial Regex TechnicalIdentifierSplitRegex();

    // Pattern 4: All-caps acronym hyphen split (e.g. "MOD- BUS RTU" -> "MODBUS RTU")
    [GeneratedRegex(@"\b(\p{Lu}{2,8})-(?:\r?\n|\s+)(\p{Lu}{2,8})\b")]
    private static partial Regex AcronymHyphenSplitRegex();

    public static ExtractedBlock Normalize(ExtractedBlock block, NormalizationDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(diagnostics);

        // Prompt §32: Tables and Code blocks MUST NOT be passed through prose dehyphenation
        if (block is TableBlock or CodeBlock)
        {
            return block;
        }

        var repairedText = RepairDehyphenation(block.Text, diagnostics);
        return block switch
        {
            TextBlock tb => tb with { Text = repairedText },
            HeadingBlock hb => hb with { Text = repairedText },
            FigureBlock fb => fb with
            {
                Caption = RepairDehyphenation(fb.Caption, diagnostics),
                OcrText = RepairDehyphenation(fb.OcrText, diagnostics),
                SurroundingContext = RepairDehyphenation(fb.SurroundingContext, diagnostics)
            },
            _ => block
        };
    }

    public static string RepairDehyphenation(string text, NormalizationDiagnostics? diagnostics = null)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('-'))
        {
            return text;
        }

        var result = text;

        // 1. Repair word hyphen splits: "конфи-\nгурация" -> "конфигурация"
        result = LowercaseHyphenSplitRegex().Replace(result, m =>
        {
            var left = m.Groups[1].Value;
            var right = m.Groups[2].Value;

            // Only merge if both parts are in the same script (both Cyrillic or both Latin)
            if (IsSameScript(left, right))
            {
                if (diagnostics != null)
                {
                    diagnostics.DehyphenationRepairs++;
                }
                return left + right;
            }

            return m.Value;
        });

        // 2. Repair titlecase-acronym splits: "Modbus-\nTCP" -> "Modbus TCP" (Prompt §29)
        result = TitleHyphenSplitRegex().Replace(result, m =>
        {
            if (diagnostics != null)
            {
                diagnostics.DehyphenationRepairs++;
            }
            return m.Groups[1].Value + " " + m.Groups[2].Value;
        });

        // 3. Repair all-caps acronym splits: "MOD- BUS RTU" -> "MODBUS RTU"
        result = AcronymHyphenSplitRegex().Replace(result, m =>
        {
            var left = m.Groups[1].Value;
            var right = m.Groups[2].Value;
            if (IsSameScript(left, right))
            {
                if (diagnostics != null)
                {
                    diagnostics.DehyphenationRepairs++;
                }
                return left + right;
            }
            return m.Value;
        });

        // 4. Repair technical identifier splits: "RS- 485" -> "RS-485", "AI- 8UI/D" -> "AI-8UI/D"
        result = TechnicalIdentifierSplitRegex().Replace(result, m =>
        {
            if (diagnostics != null)
            {
                diagnostics.DehyphenationRepairs++;
            }
            return m.Groups[1].Value + "-" + m.Groups[2].Value;
        });

        return result;
    }

    private static bool IsSameScript(string left, string right)
    {
        var leftIsCyrillic = left.Any(ch => ch >= '\u0400' && ch <= '\u04FF');
        var rightIsCyrillic = right.Any(ch => ch >= '\u0400' && ch <= '\u04FF');
        return leftIsCyrillic == rightIsCyrillic;
    }
}

