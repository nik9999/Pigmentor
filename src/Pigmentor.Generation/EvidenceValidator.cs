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
    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9]*-[A-Za-z0-9/]+\b")]
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
    /// Validates that factual claims in the answer (model IDs, ranges, tolerances, percentages, unit values, prices,
    /// table row binding, extremum operations, and category fidelity) have corresponding evidence in cited sources.
    /// </summary>
    public static bool ValidateEvidence(
        string answer,
        IReadOnlyList<Citation> citations,
        string? question = null)
    {
        return ValidateEvidenceWithReason(answer, citations, question).IsValid;
    }

    /// <summary>
    /// Validates evidence and returns whether it is valid along with a diagnostic reason explaining any failure.
    /// </summary>
    public static (bool IsValid, string? Reason) ValidateEvidenceWithReason(
        string answer,
        IReadOnlyList<Citation> citations,
        string? question = null)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return (false, "Ответ пуст или содержит только пробелы");
        }

        if (citations.Count == 0)
        {
            return (false, "Отсутствуют обязательные ссылки [S#] на источники контекста");
        }

        // 1. Table row/category fidelity (voltage under current or current under voltage)
        if (!ValidateCategoryFidelity(answer))
        {
            return (false, "Нарушена привязка к категориям таблицы (значения напряжения указаны в категории тока или наоборот)");
        }

        // 2. Citation source fidelity against question technical criteria
        var (isCitValid, citReason) = ValidateCitationFidelity(answer, citations, question);
        if (!isCitValid)
        {
            return (false, citReason);
        }

        // Extract structured markdown tables from citations
        var tables = ExtractTables(citations);

        // 3. Structured table row binding
        var (isRowBindingValid, rowBindingReason) = ValidateTableRowBinding(answer, tables, question);
        if (!isRowBindingValid)
        {
            return (false, rowBindingReason);
        }

        // 4. Structured table extremum fidelity (filter + max/min)
        var (isExtremumValid, extremumReason) = ValidateTableExtremum(answer, tables, question);
        if (!isExtremumValid)
        {
            return (false, extremumReason);
        }

        var combinedSources = string.Join(" ", citations.Select(c => c.Snippet ?? string.Empty));
        var cleanSources = CleanWhitespace(combinedSources);

        // 5. Model / product identifiers
        var modelMatches = ModelIdentifierRegex().Matches(answer);
        foreach (Match m in modelMatches)
        {
            var id = m.Value;
            if (ExcludedIdentifiers.Contains(id))
            {
                continue;
            }

            var inSnippet = ContainsModelIdentifier(cleanSources, id);
            var inMeta = citations.Any(c => (c.Title != null && ContainsModelIdentifier(c.Title, id)) ||
                                            (c.DocumentId != null && ContainsModelIdentifier(c.DocumentId, id)) ||
                                            (c.Metadata?.Title != null && ContainsModelIdentifier(c.Metadata.Title, id)) ||
                                            (c.Metadata?.Id != null && ContainsModelIdentifier(c.Metadata.Id, id)));
            if (!inSnippet && !inMeta)
            {
                return (false, $"Идентификатор модели '{id}' не найден в цитируемых источниках или их метаданных");
            }
        }

        // 6. Tolerance values (e.g. 24 В ±10 %, 120 Ом ±5 %)
        var toleranceMatches = ToleranceValueRegex().Matches(answer);
        foreach (Match m in toleranceMatches)
        {
            var num = m.Groups["num"].Value;
            var unit = m.Groups["unit"].Value;
            var tol = m.Groups["tol"].Value;
            if (!IsToleranceGrounded(num, unit, tol, cleanSources))
            {
                return (false, $"Допуск '{num} {unit} ±{tol}%' не подтверждён цитируемыми источниками");
            }
        }

        // 7. Numeric ranges with units (e.g. (-20…+20) мА, 0…20 мА, 4…20 мА, 4-20 мА)
        var rangeMatches = RangeWithUnitRegex().Matches(answer);
        foreach (Match m in rangeMatches)
        {
            var low = m.Groups["low"].Value;
            var high = m.Groups["high"].Value;
            var unit = m.Groups["unit"].Value;
            if (!IsRangeGrounded(low, high, unit, cleanSources))
            {
                return (false, $"Числовой диапазон '{low}..{high} {unit}' не подтверждён цитируемыми источниками");
            }
        }

        // 8. Standalone percentages (not already part of a tolerance value)
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
                return (false, $"Процентное значение '{m.Value}' не найдено в цитируемых источниках");
            }
        }

        // 9. Standalone unit values (not part of range or tolerance)
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
                return (false, $"Значение с единицей измерения '{m.Value}' не подтверждено цитируемыми источниками");
            }
        }

        // 10. Prices (supporting exact stated prices and derived arithmetic when base price is grounded)
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
                    return (false, "Заявленная цена не подтверждена базовой ценой из цитируемых источников");
                }
            }
        }

        return (true, null);
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
        var pattern = @"(?<![A-Za-zА-Яа-я0-9_-])" + Regex.Escape(word) + @"(?![A-Za-zА-Яа-я0-9_-])";
        return Regex.IsMatch(source, pattern, RegexOptions.IgnoreCase);
    }

    private static bool ContainsModelIdentifier(string text, string id)
    {
        var pattern = @"(?<![A-Za-zА-Яа-я0-9])" + Regex.Escape(id) + @"(?![A-Za-zА-Яа-я0-9])";
        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);
    }

    private static bool ContainsExactPhrase(string source, string phrase)
    {
        var pattern = @"(?<![A-Za-zА-Яа-я0-9_-])" + Regex.Escape(phrase) + @"(?![A-Za-zА-Яа-я0-9_-])";
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

    private static bool IsTableSeparatorLine(string line)
    {
        var trimmed = line.Replace("|", "").Replace("-", "").Replace(":", "").Replace(" ", "");
        return trimmed.Length == 0 && line.Contains("---");
    }

    private static List<string> SplitTableCells(string line)
    {
        var rawParts = line.Split('|');
        var cells = new List<string>();
        var start = line.StartsWith('|') ? 1 : 0;
        var end = line.EndsWith('|') ? rawParts.Length - 1 : rawParts.Length;
        for (var i = start; i < end; i++)
        {
            cells.Add(rawParts[i].Trim());
        }
        return cells;
    }

    private static List<ParsedTable> ExtractTables(IReadOnlyList<Citation> citations)
    {
        var tables = new List<ParsedTable>();
        foreach (var citation in citations)
        {
            if (string.IsNullOrWhiteSpace(citation.Snippet))
            {
                continue;
            }

            var lines = citation.Snippet.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            List<string>? currentHeaders = null;
            var currentRows = new List<TableRowData>();
            var inTable = false;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (!line.Contains('|'))
                {
                    if (inTable && currentRows.Count > 0)
                    {
                        tables.Add(new ParsedTable(currentHeaders, [.. currentRows], citation));
                        currentHeaders = null;
                        currentRows.Clear();
                        inTable = false;
                    }
                    continue;
                }

                if (IsTableSeparatorLine(line))
                {
                    if (currentRows.Count > 0)
                    {
                        currentHeaders = [.. currentRows[^1].Cells];
                        currentRows.RemoveAt(currentRows.Count - 1);
                    }
                    inTable = true;
                    continue;
                }

                var cells = SplitTableCells(line);
                if (cells.Count >= 2)
                {
                    inTable = true;
                    var entityKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var distinctValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    for (var c = 0; c < cells.Count; c++)
                    {
                        var cell = cells[c];
                        if (string.IsNullOrWhiteSpace(cell))
                        {
                            continue;
                        }

                        if (c == 0 && cell.Length <= 40 && !Regex.IsMatch(cell, @"^\s*[-#\d\.,]+\s*$"))
                        {
                            entityKeys.Add(cell);
                        }

                        var modelMatches = ModelIdentifierRegex().Matches(cell);
                        foreach (Match mm in modelMatches)
                        {
                            if (!ExcludedIdentifiers.Contains(mm.Value))
                            {
                                entityKeys.Add(mm.Value);
                            }
                        }

                        distinctValues.Add(cell);
                        var cleanCell = cell.Replace(" ", "").Replace("\u00A0", "");
                        if (cleanCell != cell)
                        {
                            distinctValues.Add(cleanCell);
                        }

                        // Extract numbers from data cells (excluding model identifiers)
                        if (!entityKeys.Contains(cell) && !ModelIdentifierRegex().IsMatch(cell))
                        {
                            var numMatches = Regex.Matches(cell, @"(?:\b|\()(?:\+|-)?[0-9]+(?:[\.,][0-9]+)?(?:\b|\))");
                            foreach (Match nm in numMatches)
                            {
                                var numStr = nm.Value.Trim('(', ')', '+', '-');
                                if (!string.IsNullOrWhiteSpace(numStr) && numStr != "0")
                                {
                                    distinctValues.Add(numStr);
                                }
                            }
                        }
                    }

                    currentRows.Add(new TableRowData(cells, entityKeys, distinctValues, citation, line));
                }
            }

            if (inTable && currentRows.Count > 0)
            {
                tables.Add(new ParsedTable(currentHeaders, [.. currentRows], citation));
            }
        }

        return tables;
    }

    private static (bool IsValid, string? Reason) ValidateTableRowBinding(
        string answer,
        List<ParsedTable> tables,
        string? question)
    {
        if (tables.Count == 0)
        {
            return (true, null);
        }

        var statements = SplitIntoStatements(answer);

        foreach (var table in tables)
        {
            if (table.Rows.Count < 2)
            {
                continue;
            }

            // Check if the question uniquely targets a specific row in this table
            TableRowData? questionTargetRow = null;
            if (!string.IsNullOrWhiteSpace(question))
            {
                var questionMatches = new List<TableRowData>();
                foreach (var row in table.Rows)
                {
                    if (row.EntityKeys.Any(k => ContainsExactWord(question, k)))
                    {
                        questionMatches.Add(row);
                    }
                }

                if (questionMatches.Count == 1)
                {
                    questionTargetRow = questionMatches[0];
                }
            }

            foreach (var statement in statements)
            {
                var matchedRows = new List<TableRowData>();
                foreach (var row in table.Rows)
                {
                    if (row.EntityKeys.Any(k => ContainsExactWord(statement, k)))
                    {
                        matchedRows.Add(row);
                    }
                }

                TableRowData? targetRow = null;
                if (matchedRows.Count == 1)
                {
                    targetRow = matchedRows[0];
                }
                else if (matchedRows.Count == 0 && questionTargetRow != null)
                {
                    targetRow = questionTargetRow;
                }

                if (targetRow != null)
                {
                    var targetKey = targetRow.EntityKeys.FirstOrDefault() ?? "целевой строки";

                    foreach (var otherRow in table.Rows)
                    {
                        if (otherRow == targetRow)
                        {
                            continue;
                        }

                        var otherKey = otherRow.EntityKeys.FirstOrDefault() ?? "другой строки";

                        foreach (var valStr in otherRow.DistinctValues)
                        {
                            if (targetRow.DistinctValues.Contains(valStr))
                            {
                                continue;
                            }

                            if (!string.IsNullOrWhiteSpace(question) && ContainsExactPhrase(question, valStr))
                            {
                                continue;
                            }

                            if (ContainsValueToken(statement, valStr))
                            {
                                if (IsDerivedArithmeticResult(statement, valStr, targetRow))
                                {
                                    continue;
                                }

                                return (false, $"Значение '{valStr}' относится к строке '{otherKey}', а не к '{targetKey}' (row-binding violation)");
                            }
                        }
                    }
                }
            }
        }

        return (true, null);
    }

    private static bool ContainsValueToken(string statement, string valStr)
    {
        if (string.IsNullOrWhiteSpace(valStr) || valStr == "0")
        {
            return false;
        }

        var cleanVal = valStr.Replace(" ", "").Replace("\u00A0", "");
        var pattern = @"(?<![A-Za-zА-Яа-я0-9_-]|[0-9][\.,])" + Regex.Escape(valStr) + @"(?![A-Za-zА-Яа-я0-9_-]|[\.,][0-9])";
        if (Regex.IsMatch(statement, pattern, RegexOptions.IgnoreCase))
        {
            return true;
        }

        if (cleanVal != valStr && cleanVal.Length >= 4)
        {
            var patternClean = @"(?<![A-Za-zА-Яа-я0-9_-]|[0-9][\.,])" + Regex.Escape(cleanVal) + @"(?![A-Za-zА-Яа-я0-9_-]|[\.,][0-9])";
            if (Regex.IsMatch(statement, patternClean, RegexOptions.IgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDerivedArithmeticResult(string statement, string valStr, TableRowData targetRow)
    {
        var eqIndex = statement.IndexOf('=');
        if (eqIndex >= 0)
        {
            var match = Regex.Match(statement, @"(?<![A-Za-zА-Яа-я0-9_-])" + Regex.Escape(valStr) + @"(?![A-Za-zА-Яа-я0-9_-])");
            if (match.Success && match.Index > eqIndex)
            {
                var beforeEq = statement[..eqIndex];
                if (!Regex.IsMatch(beforeEq, @"(?<![A-Za-zА-Яа-я0-9_-])" + Regex.Escape(valStr) + @"(?![A-Za-zА-Яа-я0-9_-])"))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static List<string> SplitIntoStatements(string text)
    {
        var rawLines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var statements = new List<string>();
        foreach (var rawLine in rawLines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var sentences = Regex.Split(line, @"(?<=[a-zа-яА-Я0-9\)])[.;]\s+");
            foreach (var s in sentences)
            {
                var trimmed = s.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    statements.Add(trimmed);
                }
            }
        }
        return statements;
    }

    private static (bool IsValid, string? Reason) ValidateTableExtremum(
        string answer,
        List<ParsedTable> tables,
        string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return (true, null);
        }

        if (!IsExtremumQuestion(question, out var isMaximum))
        {
            return (true, null);
        }

        if (tables.Count == 0)
        {
            return (true, null);
        }

        foreach (var table in tables)
        {
            if (table.Rows.Count < 2)
            {
                continue;
            }

            var matchingRows = FindFilterMatchingRows(table, question);
            if (matchingRows.Count < 2)
            {
                continue;
            }

            var compCol = FindComparisonColumn(matchingRows);
            if (compCol < 0)
            {
                continue;
            }

            var rowValues = new List<(TableRowData Row, double Value)>();
            foreach (var r in matchingRows)
            {
                if (compCol < r.Cells.Count && TryParseCellNumber(r.Cells[compCol], out var num))
                {
                    rowValues.Add((r, num));
                }
            }

            if (rowValues.Count < 2)
            {
                continue;
            }

            var targetVal = isMaximum
                ? rowValues.Max(rv => rv.Value)
                : rowValues.Min(rv => rv.Value);

            var winningRows = rowValues
                .Where(rv => Math.Abs(rv.Value - targetVal) < 0.0001)
                .Select(rv => rv.Row)
                .ToList();

            var anyWinnerMentioned = winningRows.Any(w => w.EntityKeys.Any(k => ContainsExactWord(answer, k)));

            foreach (var (row, val) in rowValues)
            {
                if (winningRows.Contains(row))
                {
                    continue;
                }

                var rowKey = row.EntityKeys.FirstOrDefault();
                if (rowKey != null && ContainsExactWord(answer, rowKey))
                {
                    if (!anyWinnerMentioned)
                    {
                        var winnerKey = winningRows[0].EntityKeys.FirstOrDefault() ?? "другой модели";
                        var opName = isMaximum ? "максимальное" : "минимальное";
                        return (false, $"Объект '{rowKey}' имеет значение {val}, тогда как {opName} значение {targetVal} у '{winnerKey}' (extremum violation)");
                    }
                }
            }
        }

        return (true, null);
    }

    private static bool IsExtremumQuestion(string question, out bool isMaximum)
    {
        isMaximum = true;
        if (string.IsNullOrWhiteSpace(question))
        {
            return false;
        }

        var lower = question.ToLowerInvariant();
        if (lower.Contains("наибольш") || lower.Contains("максимальн") || lower.Contains("максимум") ||
            lower.Contains("maximum") || lower.Contains("largest") || lower.Contains("highest") ||
            Regex.IsMatch(lower, @"\bmax\b"))
        {
            isMaximum = true;
            return true;
        }
        if (lower.Contains("наименьш") || lower.Contains("минимальн") || lower.Contains("минимум") ||
            lower.Contains("minimum") || lower.Contains("smallest") || lower.Contains("lowest") ||
            Regex.IsMatch(lower, @"\bmin\b"))
        {
            isMaximum = false;
            return true;
        }
        return false;
    }

    private static List<TableRowData> FindFilterMatchingRows(ParsedTable table, string question)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "какой", "какая", "какое", "какие", "товар", "товары", "объект", "объекты",
            "модель", "модели", "прибор", "приборы", "датчик", "датчики", "с", "со", "и",
            "или", "в", "во", "на", "по", "для", "из", "от", "до", "не", "что", "кто", "где",
            "как", "имеет", "имеют", "указан", "указано", "указаны", "наибольший", "наименьший",
            "максимальный", "минимальный", "максимум", "минимум", "гарантийный", "срок",
            "свойство", "значение", "параметр", "характеристика", "the", "a", "an", "which",
            "what", "has", "have", "with", "maximum", "minimum", "largest", "smallest",
            "highest", "lowest", "value", "column", "table"
        };

        var matchedTerms = new List<string>();
        foreach (var row in table.Rows)
        {
            foreach (var cell in row.Cells)
            {
                var cellTrimmed = cell.Trim();
                if (cellTrimmed.Length >= 2 && !stopWords.Contains(cellTrimmed) && ContainsExactPhrase(question, cellTrimmed))
                {
                    if (!matchedTerms.Contains(cellTrimmed, StringComparer.OrdinalIgnoreCase))
                    {
                        matchedTerms.Add(cellTrimmed);
                    }
                }
            }
        }

        if (matchedTerms.Count == 0)
        {
            var qWords = Regex.Matches(question, @"\b[A-Za-zА-Яа-я0-9_-]{3,}\b")
                              .Select(m => m.Value)
                              .Where(w => !stopWords.Contains(w))
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .ToList();

            foreach (var w in qWords)
            {
                if (table.Rows.Any(r => r.Cells.Any(c => ContainsExactWord(c, w))))
                {
                    matchedTerms.Add(w);
                }
            }
        }

        if (matchedTerms.Count > 0)
        {
            var filtered = table.Rows.Where(r => matchedTerms.Any(t => r.Cells.Any(c => c.Contains(t, StringComparison.OrdinalIgnoreCase)))).ToList();
            if (filtered.Count >= 2)
            {
                return filtered;
            }
        }

        return [.. table.Rows];
    }

    private static int FindComparisonColumn(List<TableRowData> rows)
    {
        var numCols = rows[0].Cells.Count;
        for (var c = numCols - 1; c >= 0; c--)
        {
            var numbers = new List<double>();
            foreach (var r in rows)
            {
                if (c < r.Cells.Count && TryParseCellNumber(r.Cells[c], out var num))
                {
                    numbers.Add(num);
                }
            }
            if (numbers.Count == rows.Count && numbers.Distinct().Count() > 1)
            {
                return c;
            }
        }

        for (var c = 0; c < numCols; c++)
        {
            var numbers = new List<double>();
            foreach (var r in rows)
            {
                if (c < r.Cells.Count && TryParseCellNumber(r.Cells[c], out var num))
                {
                    numbers.Add(num);
                }
            }
            if (numbers.Count >= 2 && numbers.Distinct().Count() > 1)
            {
                return c;
            }
        }

        return -1;
    }

    private static bool TryParseCellNumber(string text, out double number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var m = Regex.Match(text.Trim(), @"(?:\b|\()(?:\+|-)?[0-9]+(?:[\.,][0-9]+)?");
        if (m.Success)
        {
            var clean = m.Value.Replace(',', '.').TrimStart('+');
            return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }
        return false;
    }

    private static (bool IsValid, string? Reason) ValidateCitationFidelity(
        string answer,
        IReadOnlyList<Citation> citations,
        string? question)
    {
        var tables = ExtractTables(citations);
        if (tables.Count == 0 || string.IsNullOrWhiteSpace(question))
        {
            return (true, null);
        }

        // If the question is an extremum or filter over a structured table
        if (IsExtremumQuestion(question, out _) && (question.Contains("гаранти", StringComparison.OrdinalIgnoreCase) || question.Contains("цен", StringComparison.OrdinalIgnoreCase)))
        {
            var hasRelevantTable = tables.Any(t =>
                (t.Headers != null && t.Headers.Any(h => h.Contains("гаранти", StringComparison.OrdinalIgnoreCase) || h.Contains("цен", StringComparison.OrdinalIgnoreCase))) ||
                t.Rows.Any(r => r.Cells.Any(c => c.Contains("гаранти", StringComparison.OrdinalIgnoreCase) || c.Contains("мес", StringComparison.OrdinalIgnoreCase))));
            if (!hasRelevantTable)
            {
                return (false, "Цитируемые источники не содержат таблицу с запрошенными характеристиками (гарантия/цена)");
            }
        }

        return (true, null);
    }

    internal sealed record TableRowData(
        IReadOnlyList<string> Cells,
        HashSet<string> EntityKeys,
        HashSet<string> DistinctValues,
        Citation Citation,
        string RawLine);

    internal sealed record ParsedTable(
        IReadOnlyList<string>? Headers,
        IReadOnlyList<TableRowData> Rows,
        Citation Citation);

#if DEBUG
    /// <summary>
    /// Debug diagnostic helper that explains the exact reason why evidence was rejected.
    /// Does not alter production logic or behavior.
    /// </summary>
    public static (bool IsValid, string? Reason) DiagnoseEvidence(string answer, IReadOnlyList<Citation> citations, string? question = null)
    {
        return ValidateEvidenceWithReason(answer, citations, question);
    }
#endif
}
