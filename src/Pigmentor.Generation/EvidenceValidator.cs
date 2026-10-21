using System.Globalization;
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

    [GeneratedRegex(@"(?:\b|\()(?<low>-?\d+(?:[\.,]\d+)?)\s*(?:…|\.\.\.|–|—|-|\bдо\b)\s*\+?(?<high>\d+(?:[\.,]\d+)?)\)?\s*(?<unit>°C|В|мВ|мА|мкА|бар|Ом|кбод|кГц|МГц|мес|месяц\w*)", RegexOptions.IgnoreCase)]
    private static partial Regex RangeWithUnitRegex();

    [GeneratedRegex(@"(?<num>\d+(?:[\.,]\d+)?)\s*(?<unit>°C|В|мВ|мА|мкА|бар|Ом|кбод|кГц|МГц)\s*±\s*(?<tol>\d+(?:[\.,]\d+)?)\s*%", RegexOptions.IgnoreCase)]
    private static partial Regex ToleranceValueRegex();

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
    /// Validates that factual claims in the answer (model IDs, ranges, tolerances, percentages, unit values, prices)
    /// have corresponding evidence in the cited sources with table category fidelity.
    /// </summary>
    public static bool ValidateEvidence(
        string answer,
        IReadOnlyList<Citation> citations)
    {
        if (string.IsNullOrWhiteSpace(answer) || citations.Count == 0)
        {
            return false;
        }

        // 1. Table row/category fidelity
        if (!ValidateCategoryFidelity(answer))
        {
            return false;
        }

        var combinedSources = string.Join(" ", citations.Select(c => c.Snippet ?? string.Empty));
        var cleanSources = CleanWhitespace(combinedSources);

        // 2. Model / product identifiers
        var modelMatches = ModelIdentifierRegex().Matches(answer);
        foreach (Match m in modelMatches)
        {
            var id = m.Value;
            if (ExcludedIdentifiers.Contains(id))
            {
                continue;
            }

            var inSnippet = ContainsExactWord(cleanSources, id);
            var inMeta = citations.Any(c => (c.Title != null && ContainsExactWord(c.Title, id)) ||
                                            (c.DocumentId != null && ContainsExactWord(c.DocumentId, id)));
            if (!inSnippet && !inMeta)
            {
                return false;
            }
        }

        // 3. Tolerance values (e.g. 24 В ±10 %, 120 Ом ±5 %)
        var toleranceMatches = ToleranceValueRegex().Matches(answer);
        foreach (Match m in toleranceMatches)
        {
            var num = m.Groups["num"].Value;
            var unit = m.Groups["unit"].Value;
            var tol = m.Groups["tol"].Value;
            if (!IsToleranceGrounded(num, unit, tol, cleanSources))
            {
                return false;
            }
        }

        // 4. Numeric ranges with units (e.g. (-20…+20) мА, 0…20 мА, 4…20 мА, 4-20 мА)
        var rangeMatches = RangeWithUnitRegex().Matches(answer);
        foreach (Match m in rangeMatches)
        {
            var low = m.Groups["low"].Value;
            var high = m.Groups["high"].Value;
            var unit = m.Groups["unit"].Value;
            if (!IsRangeGrounded(low, high, unit, cleanSources))
            {
                return false;
            }
        }

        // 5. Standalone percentages (not already part of a tolerance value)
        var percentMatches = PercentageRegex().Matches(answer);
        foreach (Match m in percentMatches)
        {
            var isPart = toleranceMatches.Any(tm => tm.Index <= m.Index && m.Index + m.Length <= tm.Index + tm.Length);
            if (isPart)
            {
                continue;
            }

            var numPattern = BuildDecimalRegexPattern(m.Groups[1].Value);
            var percentPattern = @"\b" + numPattern + @"\s*%";
            if (!Regex.IsMatch(cleanSources, percentPattern, RegexOptions.IgnoreCase))
            {
                return false;
            }
        }

        // 6. Standalone unit values (not part of range or tolerance)
        var unitMatches = UnitValueRegex().Matches(answer);
        foreach (Match m in unitMatches)
        {
            var isPartOfRange = rangeMatches.Any(rm => rm.Index <= m.Index && m.Index + m.Length <= rm.Index + rm.Length);
            var isPartOfTolerance = toleranceMatches.Any(tm => tm.Index <= m.Index && m.Index + m.Length <= tm.Index + tm.Length);
            if (isPartOfRange || isPartOfTolerance)
            {
                continue;
            }

            var numPattern = BuildDecimalRegexPattern(m.Groups[1].Value);
            var unit = m.Groups[2].Value;
            var unitPattern = @"\b" + numPattern + @"\s*" + Regex.Escape(unit) + @"\b";
            if (Regex.IsMatch(cleanSources, unitPattern, RegexOptions.IgnoreCase))
            {
                continue;
            }

            var number = m.Groups[1].Value.Replace(',', '.');
            var numberPattern = @"\b" + numPattern + @"\b";
            var unitPrefix = unit[..Math.Min(3, unit.Length)];
            var unitWordPattern = @"\b" + Regex.Escape(unitPrefix) + @"\w*";
            if (!Regex.IsMatch(cleanSources, numberPattern) || !Regex.IsMatch(cleanSources, unitWordPattern, RegexOptions.IgnoreCase))
            {
                return false;
            }
        }

        // 7. Prices (supporting exact stated prices and derived arithmetic when base price is grounded)
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

            if (!anyPriceGrounded)
            {
                var hasBasePriceInCited = PriceRegex().IsMatch(cleanSources) ||
                                          Regex.IsMatch(cleanSources, @"\b\d{4,6}\b");
                if (!hasBasePriceInCited)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool ValidateCategoryFidelity(string answer)
    {
        var lines = answer.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        string? activeCategory = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (Regex.IsMatch(line, @"^\s*[-*#]*\s*(?:Диапазоны?\s+)?Ток(?:а|\b)", RegexOptions.IgnoreCase))
            {
                activeCategory = "Ток";
                continue;
            }
            if (Regex.IsMatch(line, @"^\s*[-*#]*\s*(?:Диапазоны?\s+)?Напряжени(?:е|я|\b)", RegexOptions.IgnoreCase))
            {
                activeCategory = "Напряжение";
                continue;
            }

            // In active category "Ток", check for voltage unit violations (В, мВ)
            if (string.Equals(activeCategory, "Ток", StringComparison.OrdinalIgnoreCase))
            {
                if (Regex.IsMatch(line, @"(?:\b|\()\s*-?\d+(?:[\.,]\d+)?\s*(?:…|\.\.\.|-|\bдо\b)\s*\+?\d+(?:[\.,]\d+)?\)?\s*(?:В|мВ)\b", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(line, @"\b\d+(?:[\.,]\d+)?\s*(?:В|мВ)\b", RegexOptions.IgnoreCase))
                {
                    return false;
                }
            }
            // In active category "Напряжение", check for current unit violations (мА, мкА)
            else if (string.Equals(activeCategory, "Напряжение", StringComparison.OrdinalIgnoreCase))
            {
                if (Regex.IsMatch(line, @"(?:\b|\()\s*-?\d+(?:[\.,]\d+)?\s*(?:…|\.\.\.|-|\bдо\b)\s*\+?\d+(?:[\.,]\d+)?\)?\s*(?:мА|мкА)\b", RegexOptions.IgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string BuildDecimalRegexPattern(string decimalStr)
    {
        var clean = decimalStr.Replace(',', '.');
        if (clean.Contains('.'))
        {
            var withComma = clean.Replace('.', ',');
            return @"(?:" + Regex.Escape(clean) + "|" + Regex.Escape(withComma) + ")";
        }
        return Regex.Escape(clean);
    }

    private static bool IsToleranceGrounded(string num, string unit, string tol, string sources)
    {
        var numPattern = BuildDecimalRegexPattern(num);
        var tolPattern = BuildDecimalRegexPattern(tol);
        var unitEsc = Regex.Escape(unit);

        var pattern = @"\b" + numPattern + @"\s*" + unitEsc + @"\s*±\s*" + tolPattern + @"\s*%";
        return Regex.IsMatch(sources, pattern, RegexOptions.IgnoreCase);
    }

    private static bool IsRangeGrounded(string low, string high, string unit, string sources)
    {
        var lowClean = low.Replace(',', '.');
        var highClean = high.Replace(',', '.');
        var unitEsc = Regex.Escape(unit);

        if (!double.TryParse(lowClean, NumberStyles.Float, CultureInfo.InvariantCulture, out var lowVal) ||
            !double.TryParse(highClean, NumberStyles.Float, CultureInfo.InvariantCulture, out var highVal))
        {
            return false;
        }

        var lowAbs = Math.Abs(lowVal).ToString(CultureInfo.InvariantCulture);
        var highStr = Math.Abs(highVal).ToString(CultureInfo.InvariantCulture);

        // 1. Direct match with unit (including optional pipe table delimiter)
        // e.g. "(-20…+20) | мА", "(-20…+20) мА", "-20…+20 мА", "(0…20) | мА", "(4…20) | мА"
        var pattern = @"(?:\b|\()(?:\+|-)?" + Regex.Escape(lowAbs) +
                      @"\s*(?:…|\.\.\.|–|—|-|\bдо\b)\s*\+?" + Regex.Escape(highStr) +
                      @"\)?(?:\s*\|\s*|\s+)" + unitEsc + @"\b";

        if (Regex.IsMatch(sources, pattern, RegexOptions.IgnoreCase))
        {
            return true;
        }

        // 2. Tabular format where range is in cell and unit is in header
        var rangePattern = @"(?:\b|\()(?:\+|-)?" + Regex.Escape(lowAbs) +
                           @"\s*(?:…|\.\.\.|–|—|-|\bдо\b)\s*\+?" + Regex.Escape(highStr) + @"\)?";
        var unitPrefix = unit[..Math.Min(3, unit.Length)];
        var unitPattern = @"\b" + Regex.Escape(unitPrefix) + @"\w*";

        return Regex.IsMatch(sources, rangePattern, RegexOptions.IgnoreCase) &&
               Regex.IsMatch(sources, unitPattern, RegexOptions.IgnoreCase);
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
