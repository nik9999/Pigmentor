using System.Text.RegularExpressions;

namespace Pigmentor.Generation;

/// <summary>
/// Conservative deterministic evidence validator for factual RAG claims.
/// Validates that numbers, prices, percentages, ranges, and model identifiers
/// claimed in the answer are grounded in the cited source snippets.
/// </summary>
public static partial class EvidenceValidator
{
    [GeneratedRegex(@"\b[A-Za-z]{2,}[A-Za-z0-9]*-[A-Za-z0-9/]+\b")]
    private static partial Regex ModelIdentifierRegex();

    [GeneratedRegex(@"\b(\d+(?:[\.,]\d+)?)\s*%")]
    private static partial Regex PercentageRegex();

    [GeneratedRegex(@"\b(\d+(?:[\.,]\d+)?)\s*(°C|В|мВ|мА|мкА|бар|Ом|кбод|кГц|МГц|мес|месяц\w*)", RegexOptions.IgnoreCase)]
    private static partial Regex UnitValueRegex();

    [GeneratedRegex(@"\b(\d[\d\s]{2,}\d)\s*(?:руб\w*|\$|€)", RegexOptions.IgnoreCase)]
    private static partial Regex PriceRegex();

    private static readonly HashSet<string> ExcludedIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "RS-485", "RS-232", "MODBUS-RTU", "MODBUS-TCP", "MODBUS"
    };

    /// <summary>
    /// Validates that factual claims in the answer (model IDs, percentages, unit values, prices)
    /// have corresponding evidence in the cited sources.
    /// </summary>
    public static bool ValidateEvidence(
        string answer,
        IReadOnlyList<Citation> citations)
    {
        if (string.IsNullOrWhiteSpace(answer) || citations.Count == 0)
        {
            return false;
        }

        var combinedSources = string.Join(" ", citations.Select(c => c.Snippet ?? string.Empty));
        var cleanSources = CleanWhitespace(combinedSources);

        // 1. Verify model/product identifiers (e.g. TM-200, AI-8X, PR-25, PX-400)
        var modelMatches = ModelIdentifierRegex().Matches(answer);
        foreach (Match m in modelMatches)
        {
            var id = m.Value;
            if (ExcludedIdentifiers.Contains(id))
            {
                continue;
            }

            // Must appear in cited sources (snippet, document title, or document ID)
            var inSnippet = ContainsExactWord(cleanSources, id);
            var inMeta = citations.Any(c => (c.Title != null && ContainsExactWord(c.Title, id)) ||
                                            (c.DocumentId != null && ContainsExactWord(c.DocumentId, id)));
            if (!inSnippet && !inMeta)
            {
                return false;
            }
        }

        // 2. Verify percentages (e.g. 7 %, 10 %)
        var percentMatches = PercentageRegex().Matches(answer);
        foreach (Match m in percentMatches)
        {
            var number = m.Groups[1].Value.Replace(',', '.');
            var percentPattern = @"\b" + Regex.Escape(number) + @"\s*%";
            if (!Regex.IsMatch(cleanSources, percentPattern, RegexOptions.IgnoreCase))
            {
                return false;
            }
        }

        // 3. Verify unit-measured values (e.g. 24 В, 55 °C, 70 °C, 36 месяцев)
        var unitMatches = UnitValueRegex().Matches(answer);
        foreach (Match m in unitMatches)
        {
            var number = m.Groups[1].Value.Replace(',', '.');
            var unit = m.Groups[2].Value;
            var unitPattern = @"\b" + Regex.Escape(number) + @"\s*" + Regex.Escape(unit) + @"\b";
            if (Regex.IsMatch(cleanSources, unitPattern, RegexOptions.IgnoreCase))
            {
                continue;
            }

            // For tabular sources, the unit may be in the column header while the number is in a table cell.
            // Check that the exact number exists as a standalone token and the unit appears in the cited text.
            var numberPattern = @"\b" + Regex.Escape(number) + @"\b";
            var unitPrefix = unit[..Math.Min(3, unit.Length)];
            var unitWordPattern = @"\b" + Regex.Escape(unitPrefix) + @"\w*";
            if (!Regex.IsMatch(cleanSources, numberPattern) || !Regex.IsMatch(cleanSources, unitWordPattern, RegexOptions.IgnoreCase))
            {
                return false;
            }
        }

        // 4. Verify prices if explicitly stated with currency (e.g. 19600 руб, 22100 руб)
        var priceMatches = PriceRegex().Matches(answer);
        if (priceMatches.Count > 0)
        {
            var anyPriceGrounded = false;
            foreach (Match m in priceMatches)
            {
                var priceStr = m.Groups[1].Value.Replace(" ", "").Replace("\u00A0", "");
                if (int.TryParse(priceStr, out var priceVal) && priceVal >= 1000)
                {
                    var pricePattern = @"\b" + Regex.Escape(priceStr) + @"\b|" +
                                       @"\b" + Regex.Escape(InsertSpace(priceStr)) + @"\b";
                    if (Regex.IsMatch(cleanSources, pricePattern, RegexOptions.IgnoreCase))
                    {
                        anyPriceGrounded = true;
                        break;
                    }
                }
            }

            // If price claims were made, at least one stated price must be grounded in cited sources
            if (!anyPriceGrounded && priceMatches.Count > 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsExactWord(string source, string word)
    {
        var pattern = @"(?<![A-Za-z0-9])" + Regex.Escape(word) + @"(?![A-Za-z0-9])";
        return Regex.IsMatch(source, pattern, RegexOptions.IgnoreCase);
    }

    private static string CleanWhitespace(string text)
    {
        return text.Replace("\r", " ")
                   .Replace("\n", " ")
                   .Replace("\u00A0", " ")
                   .Trim();
    }

    private static string InsertSpace(string digits)
    {
        if (digits.Length > 3)
        {
            return digits[..^3] + " " + digits[^3..];
        }
        return digits;
    }
}

