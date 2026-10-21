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

    [GeneratedRegex(@"\b(?:стоимост\w*|стои[тт]\w*|цен\w*|заказ\w*|расчет\w*|расчёт\w*|рассчит\w*|итог\w*|купить|покупк\w*|оплат\w*|cost\w*|price\w*|total\w*|order\w*|calculat\w*|comput\w*|purchas\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CalculationQuestionRegex();

    [GeneratedRegex(@"\b(?:цен\w*|стоимост\w*|price\w*|cost\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PriceHeaderRegex();

    [GeneratedRegex(
        @"(?:(?:стоимост\w*|цена)\s+(?:заказа\s+)?(?:до\s+(?:применения\s+)?скидк\w*|без\s+(?:учета\s+|учёта\s+)?скидк\w*)|(?:до\s+(?:применения\s+)?скидк\w*|без\s+(?:учета\s+|учёта\s+)?скидк\w*)|базов\w*\s+(?:стоимост\w*|цена))\s*(?:[:—–\-]|составляет|\bравна\b|\bбудет\b)?\s*(?<val>\d[\d\s]{1,}\d|\d+)(?:[\.,]\d{1,2})?\b(?!\s*(?:руб\w*\.?|\$|€|₽)?\s*(?:×|\*|x|х))\s*(?:руб\w*\.?|\$|€|₽)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex ClaimedSubtotalPhraseRegex();

    [GeneratedRegex(
        @"(?<op1>\d[\d\s]*\d|\d+)(?:[\.,]\d{1,2})?\s*(?:руб\w*\.?|\$|€|₽)?\s*(?:×|\*|x|х)\s*(?<op2>\d[\d\s]*\d|\d+)(?:[\.,]\d{1,2})?\s*(?:руб\w*\.?|\$|€|₽)?\s*=\s*(?<res>\d[\d\s]*\d|\d+)(?:[\.,]\d{1,2})?\s*(?:руб\w*\.?|\$|€|₽)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex ArithmeticMultiplicationRegex();

    [GeneratedRegex(
        @"(?:(?:скидк\w*|discount\w*)\s*(?:составляет|\bравна\b|[:—–\-])?\s*(?<pct>\d+(?:[\.,]\d+)?)\s*%|(?<pct>\d+(?:[\.,]\d+)?)\s*%\s*(?:скидк\w*|discount\w*))",
        RegexOptions.IgnoreCase)]
    private static partial Regex ClaimedDiscountPercentRegex();

    [GeneratedRegex(
        @"(?<subtotal>\d[\d\s]*\d|\d+)(?:[\.,]\d{1,2})?\s*(?:руб\w*\.?|\$|€|₽)?\s*(?:×|\*|x|х)\s*(?:\(\s*1\s*-\s*(?<dec>0?[\.,]\d+)\s*\)|(?<mult>0?[\.,]\d+))\s*=\s*(?<res>\d[\d\s]*\d|\d+)(?:[\.,]\d{1,2})?\s*(?:руб\w*\.?|\$|€|₽)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex DiscountMultiplicationRegex();

    [GeneratedRegex(
        @"(?<subtotal>\d[\d\s]*\d|\d+)(?:[\.,]\d{1,2})?\s*(?:руб\w*\.?|\$|€|₽)?\s*(?:-|—|–)\s*(?<discount>\d[\d\s]*\d|\d+)(?:[\.,]\d{1,2})?\s*(?:руб\w*\.?|\$|€|₽)?\s*=\s*(?<res>\d[\d\s]*\d|\d+)(?:[\.,]\d{1,2})?\s*(?:руб\w*\.?|\$|€|₽)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex DiscountSubtractionRegex();

    [GeneratedRegex(
        @"(?:(?:стоимост\w*|цена|итог\w*)\s+(?:заказа\s+)?(?:после\s+(?:применения\s+)?скидк\w*|со\s+скидк\w*|с\s+учетом\s+скидк\w*|с\s+учётом\s+скидк\w*)|(?:после\s+(?:применения\s+)?скидк\w*|со\s+скидк\w*|с\s+учетом\s+скидк\w*|с\s+учётом\s+скидк\w*))\s*(?:\([^)]*\))?\s*(?:[:—–\-]|составляет|\bравна\b|\bбудет\b)?\s*(?<val>\d[\d\s]{1,}\d|\d+)(?:[\.,]\d{1,2})?\b(?!\s*(?:руб\w*\.?|\$|€|₽)?\s*(?:×|\*|x|х|-|—|–))\s*(?:руб\w*\.?|\$|€|₽)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex ClaimedFinalTotalPhraseRegex();

    [GeneratedRegex(
        @"(?:\b(?:есть\s+ли|имеется\s+ли|есть|имеется)\b.*?\b(?:на\s+склад\w*|в\s+наличи\w*)\b|\b(?:на\s+склад\w*|в\s+наличи\w*)\b.*?\b(?:есть\s+ли|имеется\s+ли|есть|имеется|ли\b)\b|\bналичи\w*\s+(?:товара\s+|модели\s+|продукции\s+|прибора\s+|датчика\w*\s+)?(?:на\s+склад\w*|[A-Za-zА-Яа-я0-9_-]+)|\b(?:каково|какое|уточните|проверьте)\s+наличи\w*\b|\bдоступ\w*\s+ли\b|\b(?:in\s+stock|available)\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex AvailabilityQuestionRegex();

    [GeneratedRegex(
        @"^\s*(?:всего|итого|total)\s*(?:,\s*(?:шт\.?|ед\.?|штук\w*|единиц\w*|units?|pcs?|pieces?)|\([^)]*\))?\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TotalStockHeaderRegex();

    [GeneratedRegex(
        @"\b(?:остаток\w*|stock|quantity|qty)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex StockHeaderRegex();

    [GeneratedRegex(
        @"(?:\b(?:нет\s+(?:точной\s+|достоверной\s+|какой-либо\s+)?информаци\w*|нет\s+данных|нет\s+сведений)\b|\bинформаци\w*(?:\s+[^\s\.,;!?]+){0,10}\s+отсутству\w*|\bотсутству\w*(?:\s+[^\s\.,;!?]+){0,10}\s+информаци\w*|\bданные(?:\s+[^\s\.,;!?]+){0,10}\s+отсутству\w*|\bотсутству\w*(?:\s+[^\s\.,;!?]+){0,10}\s+данные\b|\bсведения(?:\s+[^\s\.,;!?]+){0,10}\s+отсутству\w*|\bотсутству\w*(?:\s+[^\s\.,;!?]+){0,10}\s+сведения\b|\bнеизвестн\w*\b|\bнедостаточно\s+информаци\w*\b|\b(?:no\s+information|unknown|insufficient\s+information|not\s+specified)\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex StockUnknownAnswerRegex();

    [GeneratedRegex(
        @"(?:\b(?:нет\s+(?:в\s+наличи\w*|на\s+склад\w*)|(?:в\s+наличи\w*|на\s+склад\w*)\s+нет)\b|\b(?:отсутству\w*|не\s+доступ\w*|недоступ\w*)\s+(?:в\s+наличи\w*|на\s+склад\w*|к\s+заказу|для\s+заказа)\b|\b(?:в\s+наличи\w*|на\s+склад\w*)\s+(?:отсутству\w*|не\s+доступ\w*|недоступ\w*)\b|\bтовар\w*\s+отсутству\w*\b|\b(?:отсутству\w*|закончил\w*|не\s+доступ\w*|недоступ\w*)\b|\b(?:out\s+of\s+stock|not\s+in\s+stock|not\s+available)\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex StockNegativeAnswerRegex();

    [GeneratedRegex(
        @"(?:\b(?:есть|имеется)\s+(?:в\s+наличи\w*|на\s+склад\w*)\b|\b(?:в\s+наличи\w*|на\s+склад\w*)\s+(?:есть|имеется)\b|(?<!не\s+)(?<!не)\bдоступ\w*\s+(?:на\s+склад\w*|для\s+заказа|к\s+заказу)\b|(?<!нет\s+)(?<!не\s+)\bв\s+наличи\w*\b|\b(?:in\s+stock|available)\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex StockPositiveAnswerRegex();

    private static readonly HashSet<string> ExcludedIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "RS-485", "RS-232", "MODBUS-RTU", "MODBUS-TCP", "MODBUS"
    };

    private static readonly HashSet<string> ComparisonStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Russian interrogatives & relatives
        "какой", "какая", "какое", "какие", "какого", "какой", "каком", "какому", "какими", "каких",
        "кто", "что", "где", "как", "чем", "кому", "чему", "кого", "чего",

        // English interrogatives & relatives
        "which", "what", "who", "whom", "where", "how",

        // Russian & English generic entity terms
        "товар", "товары", "товара", "товаров",
        "объект", "объекты", "объекта", "объектов",
        "модель", "модели", "моделью", "моделей",
        "прибор", "приборы", "прибора", "приборов",
        "устройство", "устройства", "устройств", "устройством",
        "элемент", "элементы", "элемента", "элементов",
        "продукт", "продукты", "продукта", "продуктов",
        "склад", "склады", "складе", "склада", "складов", "складах",
        "item", "items", "model", "models", "device", "devices", "object", "objects", "product", "products", "warehouse", "warehouses",

        // Russian extremum words
        "наибольший", "наибольшая", "наибольшее", "наибольшие", "наибольшую", "наибольшего", "наибольшем", "наибольшим", "наибольшими", "наибольших",
        "наименьший", "наименьшая", "наименьшее", "наименьшие", "наименьшую", "наименьшего", "наименьшем", "наименьшим", "наименьшими", "наименьших",
        "максимальный", "максимальная", "максимальное", "максимальные", "максимальную", "максимального", "максимальном", "максимальным", "максимальными", "максимальных",
        "минимальный", "минимальная", "минимальное", "минимальные", "минимальную", "минимального", "минимальном", "минимальным", "минимальными", "минимальных",
        "максимум", "минимум", "самый", "самая", "самое", "самые", "самую", "самого", "самом", "самым",
        "больше", "меньше", "всего", "всех",

        // English extremum words
        "maximum", "minimum", "max", "min", "largest", "smallest", "highest", "lowest", "greatest", "least", "top", "bottom", "best", "worst",

        // Russian prepositions, conjunctions, particles, verbs
        "в", "во", "на", "с", "со", "по", "для", "из", "от", "до", "к", "ко", "у", "о", "об", "обо", "при", "через", "под", "над",
        "не", "и", "или", "а", "но", "да", "ли", "бы", "же", "только",
        "имеет", "имеют", "указан", "указана", "указано", "указаны", "находится", "находятся", "является", "являются",

        // English prepositions, conjunctions, verbs
        "in", "on", "at", "with", "by", "for", "from", "to", "of", "into", "about", "under", "over",
        "not", "and", "or", "is", "are", "was", "were", "has", "have", "had", "be", "been", "only",

        // Neutral metadata/table words
        "значение", "значения", "значением", "значений",
        "параметр", "параметры", "параметра", "параметром", "параметров",
        "характеристика", "характеристики", "характеристику", "характеристикой", "характеристик",
        "показатель", "показатели", "показателя", "показателем", "показателей",
        "свойство", "свойства", "свойством", "свойств",
        "данные", "таблица", "таблицы", "таблице", "строка", "строки", "столбец", "столбца", "список",
        "value", "values", "parameter", "parameters", "characteristic", "characteristics", "property", "properties", "data", "table", "column", "row", "list"
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

        // 2b. Calculation source row grounding for requested model
        var (isCalcSourceValid, calcSourceReason) = ValidateCalculationSourceRow(tables, question);
        if (!isCalcSourceValid)
        {
            return (false, calcSourceReason);
        }

        // 3. Structured table row binding
        var (isRowBindingValid, rowBindingReason) = ValidateTableRowBinding(answer, tables, question);
        if (!isRowBindingValid)
        {
            return (false, rowBindingReason);
        }

        // 3b. Calculation subtotal verification (unit price × quantity)
        var (isSubtotalValid, subtotalReason) = ValidateCalculationSubtotal(answer, tables, question);
        if (!isSubtotalValid)
        {
            return (false, subtotalReason);
        }

        // 3c. Calculation discount verification (grounded discount percent and final total)
        var (isDiscountValid, discountReason) = ValidateCalculationDiscount(answer, tables, question);
        if (!isDiscountValid)
        {
            return (false, discountReason);
        }

        // 3d. Stock availability verification (grounded stock and availability claim)
        var (isStockValid, stockReason) = ValidateStockAvailability(answer, tables, question);
        if (!isStockValid)
        {
            return (false, stockReason);
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

        // 1. Try horizontal extremum across columns for a specific entity
        foreach (var table in tables)
        {
            var horizontalExtremum = TryResolveHorizontalExtremum(question, table, isMaximum);
            if (horizontalExtremum != null)
            {
                return ValidateHorizontalExtremumAnswer(answer, horizontalExtremum, isMaximum);
            }
        }

        // 2. Existing vertical extremum across rows
        foreach (var table in tables)
        {
            if (table.Rows.Count < 2)
            {
                continue;
            }

            var compCol = ResolveComparisonColumn(question, table);
            if (compCol < 0)
            {
                // Ambiguity policy: could not reliably resolve comparison column from question and headers.
                // Refrain from arbitrary conclusion (no false rejection, no arbitrary column).
                continue;
            }

            var matchingRows = FindFilterMatchingRows(table, question, compCol);
            if (matchingRows.Count < 2)
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

    internal sealed record HorizontalExtremum(
        string WinningColumnName,
        decimal WinningValue,
        IReadOnlyList<(string ColumnName, decimal Value)> LosingColumns,
        string EntityName);

    internal static HorizontalExtremum? TryResolveHorizontalExtremum(
        string question,
        ParsedTable table,
        bool isMaximum)
    {
        if (string.IsNullOrWhiteSpace(question) || table.Headers == null || table.Headers.Count < 3 || table.Rows.Count == 0)
        {
            return null;
        }

        TableRowData? targetRow = null;
        string? matchedEntity = null;

        var requestedModels = FindRequestedModelIdentifiers(question);
        if (requestedModels.Count == 1)
        {
            var modelId = requestedModels[0];
            var matchingRows = table.Rows.Where(r => RowMatchesModel(r, modelId)).ToList();
            if (matchingRows.Count == 1)
            {
                targetRow = matchingRows[0];
                matchedEntity = modelId;
            }
        }
        else if (requestedModels.Count == 0)
        {
            var matchingRows = new List<(TableRowData Row, string Key)>();
            foreach (var r in table.Rows)
            {
                foreach (var k in r.EntityKeys)
                {
                    if (ContainsExactWord(question, k))
                    {
                        matchingRows.Add((r, k));
                        break;
                    }
                }
            }

            if (matchingRows.Count == 1)
            {
                targetRow = matchingRows[0].Row;
                matchedEntity = matchingRows[0].Key;
            }
        }

        if (targetRow == null || matchedEntity == null)
        {
            return null;
        }

        var candidates = new List<(string Header, decimal Value)>();

        for (var c = 0; c < table.Headers.Count; c++)
        {
            if (c >= targetRow.Cells.Count)
            {
                continue;
            }

            var header = table.Headers[c].Trim();
            var cell = targetRow.Cells[c].Trim();

            // 4. Exclude aggregate columns: Всего, Итого, Total, Sum
            if (IsAggregateHeader(header))
            {
                continue;
            }

            // 5. Exclude identifier/model column
            if (IsIdentifierHeader(header) ||
                ContainsModelIdentifier(cell, matchedEntity) ||
                targetRow.EntityKeys.Contains(cell))
            {
                continue;
            }

            if (TryParseCellDecimal(cell, out var val))
            {
                candidates.Add((header, val));
            }
        }

        if (candidates.Count < 2)
        {
            return null;
        }

        var targetVal = isMaximum
            ? candidates.Max(c => c.Value)
            : candidates.Min(c => c.Value);

        var winners = candidates.Where(c => c.Value == targetVal).ToList();
        if (winners.Count != 1)
        {
            // Ambiguity policy: tie for extremum => fail-open
            return null;
        }

        var winning = winners[0];
        var losing = candidates.Where(c => c != winning).ToList();

        return new HorizontalExtremum(winning.Header, winning.Value, losing, matchedEntity);
    }

    private static (bool IsValid, string? Reason) ValidateHorizontalExtremumAnswer(
        string answer,
        HorizontalExtremum extremum,
        bool isMaximum)
    {
        var opName = isMaximum ? "максимальное" : "минимальное";

        // 8. If structured evidence is resolved, "no information" / "data absent" must FAIL
        if (StockUnknownAnswerRegex().IsMatch(answer))
        {
            return (false, $"В цитируемых источниках указаны данные по модели '{extremum.EntityName}' ({opName}: {extremum.WinningColumnName} — {extremum.WinningValue:0.##}), но в ответе утверждается об отсутствии информации");
        }

        // 7. Check that the answer contains the winning column name
        if (!ContainsColumnReference(answer, extremum.WinningColumnName))
        {
            // If answer mentions a losing column name, produce clear diagnostic
            foreach (var (losingName, losingVal) in extremum.LosingColumns)
            {
                if (ContainsColumnReference(answer, losingName))
                {
                    return (false, $"Склад/колонка '{losingName}' имеет значение {losingVal:0.##}, тогда как {opName} значение {extremum.WinningValue:0.##} на складе/в колонке '{extremum.WinningColumnName}' (horizontal extremum violation)");
                }
            }

            return (false, $"В ответе не указан правильный победитель экстремума (ожидается '{extremum.WinningColumnName}' со значением {extremum.WinningValue:0.##})");
        }

        // Check that the answer contains the winning value
        if (!ContainsDecimalValue(answer, extremum.WinningValue))
        {
            return (false, $"Для объекта '{extremum.EntityName}' на складе/в колонке '{extremum.WinningColumnName}' указано значение {extremum.WinningValue:0.##}, но это значение не подтверждено в ответе");
        }

        return (true, null);
    }

    private static bool IsAggregateHeader(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        return Regex.IsMatch(header.Trim(), @"^(?:всего|итого|total|sum|сумма|суммарн\w*)(?:[\s,\(\[].*)?$", RegexOptions.IgnoreCase);
    }

    private static bool IsIdentifierHeader(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        return Regex.IsMatch(header.Trim(), @"\b(?:модель|артикул|наименование|название|тип|прибор|устройство|датчик|товар|код|model|item|name|type|id|sku|code)\b", RegexOptions.IgnoreCase);
    }

    private static bool ContainsColumnReference(string text, string columnName)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(columnName))
        {
            return false;
        }

        if (ContainsExactWord(text, columnName))
        {
            return true;
        }

        var cleanHeader = CleanBaseHeader(columnName);
        var words = Regex.Split(text, @"[^\p{L}\p{Nd}_-]+").Where(w => w.Length >= 3);
        foreach (var w in words)
        {
            if (TokensMatch(w, cleanHeader))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsDecimalValue(string text, decimal value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var valStr = value.ToString("0.##", CultureInfo.InvariantCulture);
        var pattern = @"(?<![\d\.,])" + Regex.Escape(valStr) + @"(?![\d\.,])";
        if (Regex.IsMatch(text, pattern))
        {
            return true;
        }

        var withComma = valStr.Replace('.', ',');
        var commaPattern = @"(?<![\d\.,])" + Regex.Escape(withComma) + @"(?![\d\.,])";
        return Regex.IsMatch(text, commaPattern);
    }

    private static bool TryParseCellDecimal(string text, out decimal number)
    {
        number = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = Regex.Replace(text.Trim(), @"(?<=\d)\s+(?=\d)", "");
        var m = Regex.Match(cleaned, @"(?:\b|\()(?:\+|-)?[0-9]+(?:[\.,][0-9]+)?");
        if (m.Success)
        {
            var clean = m.Value.Replace(',', '.').TrimStart('+');
            return decimal.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }
        return false;
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
            lower.Contains("больше всего") || lower.Contains("больше всех") ||
            lower.Contains("maximum") || lower.Contains("largest") || lower.Contains("highest") || lower.Contains("most") ||
            Regex.IsMatch(lower, @"\bmax\b"))
        {
            isMaximum = true;
            return true;
        }
        if (lower.Contains("наименьш") || lower.Contains("минимальн") || lower.Contains("минимум") ||
            lower.Contains("меньше всего") || lower.Contains("меньше всех") ||
            lower.Contains("minimum") || lower.Contains("smallest") || lower.Contains("lowest") || lower.Contains("least") || lower.Contains("fewest") ||
            Regex.IsMatch(lower, @"\bmin\b"))
        {
            isMaximum = false;
            return true;
        }
        return false;
    }

    internal static int ResolveComparisonColumn(
        string question,
        ParsedTable table,
        IReadOnlyList<TableRowData>? candidateRows = null)
    {
        if (table.Headers == null || table.Headers.Count == 0 || string.IsNullOrWhiteSpace(question))
        {
            return -1;
        }

        candidateRows ??= table.Rows;
        if (candidateRows.Count == 0)
        {
            return -1;
        }

        // 1. Identify valid numeric columns in candidateRows (excluding identifier/model columns)
        var numericColIndices = new List<int>();
        for (var c = 0; c < table.Headers.Count; c++)
        {
            var numbers = new List<double>();
            var isEntityCol = false;

            foreach (var r in candidateRows)
            {
                if (c < r.Cells.Count)
                {
                    var cell = r.Cells[c].Trim();
                    if (r.EntityKeys.Contains(cell) || ModelIdentifierRegex().IsMatch(cell))
                    {
                        isEntityCol = true;
                        break;
                    }

                    if (TryParseCellNumber(cell, out var val))
                    {
                        numbers.Add(val);
                    }
                }
            }

            if (!isEntityCol && numbers.Count >= Math.Min(2, candidateRows.Count) && numbers.Count > 0)
            {
                numericColIndices.Add(c);
            }
        }

        if (numericColIndices.Count == 0)
        {
            return -1;
        }

        // 2. Extract comparison target tokens from question (excluding extremum, grammar, and generic entity stopwords)
        var qTokens = ExtractQuestionComparisonTokens(question);
        if (qTokens.Count == 0)
        {
            // If question does not specify a specific property (e.g. "максимальное значение"):
            // If exactly one numeric column exists in the table, it is unambiguously the target.
            // If multiple numeric columns exist, it is ambiguous => return -1 (ambiguity policy).
            return numericColIndices.Count == 1 ? numericColIndices[0] : -1;
        }

        // 3. Score candidate numeric columns against question tokens
        var scoredColumns = new List<(int ColumnIndex, int Score)>();
        foreach (var colIdx in numericColIndices)
        {
            var rawHeader = table.Headers[colIdx];
            var headerTokens = ExtractHeaderTokens(rawHeader);
            var score = 0;

            foreach (var hToken in headerTokens)
            {
                foreach (var qToken in qTokens)
                {
                    if (TokensMatch(hToken, qToken))
                    {
                        score += 2;
                    }
                }
            }

            var baseHeader = CleanBaseHeader(rawHeader);
            if (baseHeader.Length >= 3 && ContainsExactPhrase(question, baseHeader))
            {
                score += 3;
            }

            if (score > 0)
            {
                scoredColumns.Add((colIdx, score));
            }
        }

        if (scoredColumns.Count == 0)
        {
            return -1;
        }

        // 4. Disambiguation
        var maxScore = scoredColumns.Max(sc => sc.Score);
        var winners = scoredColumns.Where(sc => sc.Score == maxScore).ToList();

        // Must have a single clear winner
        if (winners.Count == 1)
        {
            return winners[0].ColumnIndex;
        }

        // Ambiguity policy: multiple columns tied with top score => unresolved
        return -1;
    }

    private static string CleanBaseHeader(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return string.Empty;
        }

        var parts = header.Split([',', '(', '/', '['], 2);
        return parts[0].Trim();
    }

    private static List<string> ExtractHeaderTokens(string header)
    {
        var baseHeader = CleanBaseHeader(header);
        if (string.IsNullOrWhiteSpace(baseHeader))
        {
            return [];
        }

        var matches = Regex.Matches(baseHeader, @"\b[\p{L}\p{N}_-]+\b");
        var tokens = new List<string>();
        foreach (Match m in matches)
        {
            var val = m.Value.ToLowerInvariant();
            if (val.Length >= 2 && !ComparisonStopWords.Contains(val))
            {
                tokens.Add(val);
            }
        }
        return tokens;
    }

    private static List<string> ExtractQuestionComparisonTokens(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return [];
        }

        var words = Regex.Matches(question, @"\b[\p{L}\p{N}_-]+\b")
                         .Select(m => m.Value)
                         .ToList();

        var extIdx = -1;
        for (var i = 0; i < words.Count; i++)
        {
            if (IsExtremumToken(words[i]))
            {
                extIdx = i;
                break;
            }
        }

        var tokens = new List<string>();

        if (extIdx >= 0)
        {
            if (extIdx + 1 < words.Count)
            {
                // Target follows extremum keyword (e.g. "наибольший гарантийный срок", "максимальный ресурс", "максимальное значение")
                for (var i = extIdx + 1; i < Math.Min(words.Count, extIdx + 4); i++)
                {
                    var val = words[i].ToLowerInvariant();
                    if (val.Length >= 2 && !ComparisonStopWords.Contains(val) && !ModelIdentifierRegex().IsMatch(words[i]))
                    {
                        tokens.Add(val);
                    }
                }
            }
            else
            {
                // Extremum keyword is at the end of the question (e.g. "мощность наибольшая?")
                for (var i = extIdx - 1; i >= Math.Max(0, extIdx - 3); i--)
                {
                    var val = words[i].ToLowerInvariant();
                    if (val.Length >= 2 && !ComparisonStopWords.Contains(val) && !ModelIdentifierRegex().IsMatch(words[i]))
                    {
                        tokens.Add(val);
                    }
                }
            }
        }
        else
        {
            // If no explicit extremum keyword located, extract all non-stopwords
            foreach (var w in words)
            {
                var val = w.ToLowerInvariant();
                if (val.Length >= 2 && !ComparisonStopWords.Contains(val) && !ModelIdentifierRegex().IsMatch(w))
                {
                    tokens.Add(val);
                }
            }
        }

        return tokens;
    }

    private static bool IsExtremumToken(string word)
    {
        var lower = word.ToLowerInvariant();
        return lower.StartsWith("наибольш", StringComparison.Ordinal) ||
               lower.StartsWith("наименьш", StringComparison.Ordinal) ||
               lower.StartsWith("максимальн", StringComparison.Ordinal) ||
               lower.StartsWith("минимальн", StringComparison.Ordinal) ||
               lower == "максимум" ||
               lower == "минимум" ||
               lower == "больше" ||
               lower == "меньше" ||
               lower == "maximum" ||
               lower == "minimum" ||
               lower == "max" ||
               lower == "min" ||
               lower == "largest" ||
               lower == "smallest" ||
               lower == "highest" ||
               lower == "lowest" ||
               lower == "greatest" ||
               lower == "least" ||
               lower == "most" ||
               lower == "fewest";
    }

    private static bool TokensMatch(string t1, string t2)
    {
        if (string.Equals(t1, t2, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var s1 = t1.ToLowerInvariant();
        var s2 = t2.ToLowerInvariant();

        var minLen = Math.Min(s1.Length, s2.Length);
        if (minLen < 3)
        {
            return false;
        }

        var commonPrefixLen = 0;
        while (commonPrefixLen < minLen && s1[commonPrefixLen] == s2[commonPrefixLen])
        {
            commonPrefixLen++;
        }

        // 1. Meaningful common stem prefix of at least 4 characters
        // e.g. "гаранти" in "гарантия"/"гарантийный", "мощност" in "мощность", "масс" in "масса"/"массу", "ресурс" in "ресурс"/"ресурса"
        if (commonPrefixLen >= 4)
        {
            return true;
        }

        // 2. Short 3-character stem for concise root words differing only by inflectional ending
        // e.g. "цен" in "цена"/"цену", "вес" in "вес"/"веса", "ток" in "ток"/"тока"
        if (commonPrefixLen == 3 && s1.Length <= 4 && s2.Length <= 4)
        {
            return true;
        }

        return false;
    }

    private static List<TableRowData> FindFilterMatchingRows(ParsedTable table, string question, int excludedCol = -1)
    {
        var matchedTerms = new List<string>();
        foreach (var row in table.Rows)
        {
            for (var c = 0; c < row.Cells.Count; c++)
            {
                if (excludedCol >= 0 && c == excludedCol)
                {
                    continue;
                }

                var cellTrimmed = row.Cells[c].Trim();
                if (cellTrimmed.Length >= 1 && char.IsLetterOrDigit(cellTrimmed[0]) && !ComparisonStopWords.Contains(cellTrimmed) && ContainsExactPhrase(question, cellTrimmed))
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
            var qWords = Regex.Matches(question, @"\b[\p{L}\p{N}_-]+\b")
                              .Select(m => m.Value)
                              .Where(w => w.Length >= 1 && char.IsLetterOrDigit(w[0]) && !ComparisonStopWords.Contains(w))
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .ToList();

            foreach (var w in qWords)
            {
                if (table.Rows.Any(r =>
                {
                    for (var c = 0; c < r.Cells.Count; c++)
                    {
                        if (excludedCol >= 0 && c == excludedCol)
                        {
                            continue;
                        }
                        if (ContainsExactWord(r.Cells[c], w))
                        {
                            return true;
                        }
                    }
                    return false;
                }))
                {
                    matchedTerms.Add(w);
                }
            }
        }

        if (matchedTerms.Count > 0)
        {
            var filtered = table.Rows.Where(r => matchedTerms.Any(t =>
            {
                for (var c = 0; c < r.Cells.Count; c++)
                {
                    if (excludedCol >= 0 && c == excludedCol)
                    {
                        continue;
                    }
                    if (ContainsExactWord(r.Cells[c], t) || r.Cells[c].Contains(t, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            })).ToList();

            if (filtered.Count >= 2)
            {
                return filtered;
            }
        }

        return [.. table.Rows];
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
        if (string.IsNullOrWhiteSpace(question))
        {
            return (true, null);
        }

        var tables = ExtractTables(citations);
        if (tables.Count == 0)
        {
            return (true, null);
        }

        if (IsExtremumQuestion(question, out var isMaxFidelity))
        {
            var qTokens = ExtractQuestionComparisonTokens(question);
            if (qTokens.Count > 0)
            {
                var hasRelevantTable = tables.Any(t =>
                    ResolveComparisonColumn(question, t) >= 0 ||
                    TryResolveHorizontalExtremum(question, t, isMaxFidelity) != null);
                if (!hasRelevantTable)
                {
                    return (false, "Цитируемые источники не содержат таблицу с характеристиками, запрошенными для сравнения");
                }
            }
        }

        return (true, null);
    }

    private static (bool IsValid, string? Reason) ValidateCalculationSourceRow(
        IReadOnlyList<ParsedTable> tables,
        string? question)
    {
        if (string.IsNullOrWhiteSpace(question) || !IsCalculationQuestion(question))
        {
            return (true, null);
        }

        var requestedModels = FindRequestedModelIdentifiers(question);
        if (requestedModels.Count == 0)
        {
            return (true, null);
        }

        foreach (var modelId in requestedModels)
        {
            if (!CitationTablesContainEntityRowWithNumericData(tables, modelId))
            {
                return (false, $"Цитируемые источники не содержат строку таблицы для запрошенной модели '{modelId}' с исходными данными для расчёта");
            }
        }

        return (true, null);
    }

    private static bool IsCalculationQuestion(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return false;
        }

        if (IsExtremumQuestion(question, out _))
        {
            return false;
        }

        return CalculationQuestionRegex().IsMatch(question);
    }

    private static List<string> FindRequestedModelIdentifiers(string question)
    {
        var list = new List<string>();
        foreach (Match m in ModelIdentifierRegex().Matches(question))
        {
            var id = m.Value;
            if (!ExcludedIdentifiers.Contains(id) && !list.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(id);
            }
        }
        return list;
    }

    private static bool CitationTablesContainEntityRowWithNumericData(
        IReadOnlyList<ParsedTable> tables,
        string modelId)
    {
        foreach (var table in tables)
        {
            foreach (var row in table.Rows)
            {
                if (RowMatchesModel(row, modelId) && RowHasNumericData(row, modelId))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool RowMatchesModel(TableRowData row, string modelId)
    {
        if (row.EntityKeys.Contains(modelId))
        {
            return true;
        }

        for (var i = 0; i < row.Cells.Count; i++)
        {
            if (ContainsModelIdentifier(row.Cells[i], modelId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool RowHasNumericData(TableRowData row, string modelId)
    {
        if (row.DistinctValues.Any(v => double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0))
        {
            return true;
        }

        for (var i = 0; i < row.Cells.Count; i++)
        {
            var cell = row.Cells[i];
            if (ContainsModelIdentifier(cell, modelId))
            {
                continue;
            }

            var normalized = Regex.Replace(cell, @"(?<=\d)\s+(?=\d)", "");
            var m = Regex.Match(normalized, @"\b\d+(?:[\.,]\d+)?\b");
            if (m.Success && double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var val) && val > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static (bool IsValid, string? Reason) ValidateCalculationSubtotal(
        string answer,
        IReadOnlyList<ParsedTable> tables,
        string? question)
    {
        if (string.IsNullOrWhiteSpace(question) || !IsCalculationQuestion(question))
        {
            return (true, null);
        }

        var requestedModels = FindRequestedModelIdentifiers(question);
        if (requestedModels.Count == 0)
        {
            return (true, null);
        }

        foreach (var modelId in requestedModels)
        {
            var qty = ExtractRequestedQuantity(question, modelId);
            if (qty == null || qty.Value <= 0)
            {
                continue;
            }

            decimal? resolvedUnitPrice = null;
            foreach (var table in tables)
            {
                if (TryResolveUnitPrice(table, modelId, out var up))
                {
                    resolvedUnitPrice = up;
                    break;
                }
            }

            if (resolvedUnitPrice == null)
            {
                continue;
            }

            var expectedSubtotal = resolvedUnitPrice.Value * qty.Value;

            var (isValidClaim, claimReason) = VerifyClaimedSubtotal(
                answer,
                modelId,
                qty.Value,
                resolvedUnitPrice.Value,
                expectedSubtotal);

            if (!isValidClaim)
            {
                return (false, claimReason);
            }
        }

        return (true, null);
    }

    private static int? ExtractRequestedQuantity(string question, string modelId)
    {
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var modelEsc = Regex.Escape(modelId);

        // Quantity before model: "5 датчиков TM-200", "заказать 5 TM-200", "order 3 ZX-410"
        var patternBefore = @"\b(?<qty>[1-9]\d*)\s+(?:[A-Za-zА-Яа-яЁё\.,/]+\s+){0,3}" + modelEsc + @"\b";
        var matchesBefore = Regex.Matches(question, patternBefore, RegexOptions.IgnoreCase);
        if (matchesBefore.Count > 0)
        {
            var last = matchesBefore[^1];
            if (int.TryParse(last.Groups["qty"].Value, out var q) && q > 0)
            {
                return q;
            }
        }

        // Quantity after model: "TM-200 5 шт.", "TM-200 в количестве 5 шт."
        var patternAfter = @"\b" + modelEsc + @"\b\s+(?:[A-Za-zА-Яа-яЁё\.,/]+\s+){0,3}(?<qty>[1-9]\d*)\s*(?:шт|штук\w*|единиц\w*|прибор\w*|датчик\w*|unit\w*|piece\w*|pcs?|items?)\b";
        var matchAfter = Regex.Match(question, patternAfter, RegexOptions.IgnoreCase);
        if (matchAfter.Success && int.TryParse(matchAfter.Groups["qty"].Value, out var qAfter) && qAfter > 0)
        {
            return qAfter;
        }

        return null;
    }

    private static int ResolvePriceColumn(ParsedTable table)
    {
        if (table.Headers == null || table.Headers.Count == 0)
        {
            return -1;
        }

        var matchingIndices = new List<int>();
        for (var i = 0; i < table.Headers.Count; i++)
        {
            var h = table.Headers[i];
            if (PriceHeaderRegex().IsMatch(h))
            {
                matchingIndices.Add(i);
            }
        }

        if (matchingIndices.Count == 1)
        {
            return matchingIndices[0];
        }

        if (matchingIndices.Count > 1)
        {
            var baseIndex = matchingIndices.FindIndex(idx =>
                Regex.IsMatch(table.Headers[idx], @"\b(?:базов\w*|исходн\w*|до\s+скидк\w*|без\s+скидк\w*|unit\s+price|base\s+price)\b", RegexOptions.IgnoreCase));
            if (baseIndex >= 0)
            {
                return matchingIndices[baseIndex];
            }

            return -1;
        }

        return -1;
    }

    private static bool TryResolveUnitPrice(
        ParsedTable table,
        string modelId,
        out decimal unitPrice)
    {
        unitPrice = 0m;
        var priceCol = ResolvePriceColumn(table);
        if (priceCol < 0)
        {
            return false;
        }

        foreach (var row in table.Rows)
        {
            if (RowMatchesModel(row, modelId))
            {
                if (priceCol < row.Cells.Count && TryParsePriceDecimal(row.Cells[priceCol], out unitPrice))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryParsePriceDecimal(string text, out decimal price)
    {
        price = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = Regex.Replace(text.Trim(), @"(?<=\d)\s+(?=\d)", "");
        var m = Regex.Match(cleaned, @"\b\d+(?:[\.,]\d{1,2})?\b");
        if (!m.Success)
        {
            return false;
        }

        var numStr = m.Value.Replace(',', '.');
        if (decimal.TryParse(numStr, NumberStyles.Number, CultureInfo.InvariantCulture, out price) && price > 0m)
        {
            return true;
        }

        return false;
    }

    private static (bool IsValid, string? Reason) VerifyClaimedSubtotal(
        string answer,
        string modelId,
        int expectedQty,
        decimal expectedUnitPrice,
        decimal expectedSubtotal)
    {
        // 1. Check arithmetic multiplication where one operand is expectedQty
        foreach (Match m in ArithmeticMultiplicationRegex().Matches(answer))
        {
            if (TryParsePriceDecimal(m.Groups["op1"].Value, out var op1) &&
                TryParsePriceDecimal(m.Groups["op2"].Value, out var op2) &&
                TryParsePriceDecimal(m.Groups["res"].Value, out var res))
            {
                if (op1 == expectedQty || op2 == expectedQty)
                {
                    var priceOperand = (op1 == expectedQty) ? op2 : op1;
                    if (priceOperand != expectedUnitPrice)
                    {
                        return (false, $"В расчёте использована цена {priceOperand:0.##}, но для модели '{modelId}' в таблице указана цена {expectedUnitPrice:0.##}");
                    }

                    if (res != expectedSubtotal)
                    {
                        return (false, $"Заявленная стоимость до скидки {res:0.##} не соответствует расчёту {expectedUnitPrice:0.##} × {expectedQty} = {expectedSubtotal:0.##}");
                    }

                    return (true, null);
                }
            }
        }

        // 2. Check explicit subtotal phrase: "Стоимость до скидки: 98000 руб."
        var phraseMatch = ClaimedSubtotalPhraseRegex().Match(answer);
        if (phraseMatch.Success)
        {
            var rawVal = phraseMatch.Groups["val"].Value;
            if (TryParsePriceDecimal(rawVal, out var phraseAmount))
            {
                if (phraseAmount != expectedSubtotal)
                {
                    return (false, $"Заявленная стоимость до скидки {phraseAmount:0.##} не соответствует расчёту {expectedUnitPrice:0.##} × {expectedQty} = {expectedSubtotal:0.##}");
                }

                return (true, null);
            }
        }

        return (true, null);
    }

    private static (bool IsValid, string? Reason) ValidateCalculationDiscount(
        string answer,
        IReadOnlyList<ParsedTable> tables,
        string? question)
    {
        if (string.IsNullOrWhiteSpace(question) || !IsCalculationQuestion(question))
        {
            return (true, null);
        }

        var requestedModels = FindRequestedModelIdentifiers(question);
        if (requestedModels.Count == 0)
        {
            return (true, null);
        }

        foreach (var modelId in requestedModels)
        {
            var qty = ExtractRequestedQuantity(question, modelId);
            if (qty == null || qty.Value <= 0)
            {
                continue;
            }

            decimal? resolvedUnitPrice = null;
            foreach (var table in tables)
            {
                if (TryResolveUnitPrice(table, modelId, out var up))
                {
                    resolvedUnitPrice = up;
                    break;
                }
            }

            if (resolvedUnitPrice == null)
            {
                continue;
            }

            var subtotal = resolvedUnitPrice.Value * qty.Value;

            var applicableDiscount = ResolveApplicableDiscountPercent(tables, qty.Value);
            if (applicableDiscount == null || applicableDiscount.Value <= 0m)
            {
                continue;
            }

            var expectedDiscountPercent = applicableDiscount.Value;
            var discountAmount = subtotal * expectedDiscountPercent / 100m;
            var expectedFinal = subtotal - discountAmount;

            var (isDiscountValid, discountReason) = VerifyClaimedDiscount(
                answer,
                modelId,
                subtotal,
                expectedDiscountPercent,
                discountAmount,
                expectedFinal);

            if (!isDiscountValid)
            {
                return (false, discountReason);
            }
        }

        return (true, null);
    }

    private static decimal? ResolveApplicableDiscountPercent(
        IReadOnlyList<ParsedTable> tables,
        int quantity)
    {
        var applicableRules = new List<(int Threshold, decimal Percent)>();

        foreach (var table in tables)
        {
            foreach (var row in table.Rows)
            {
                var fullRowText = string.Join(" | ", row.Cells);
                var condMatch = Regex.Match(
                    fullRowText,
                    @"(?:скидк\w*\s+от|discount\s+from)\s+(?<thresh>\d+)\s*(?:единиц\w*|штук\w*|шт\.?|прибор\w*|датчик\w*|устройств\w*|модел\w*|товар\w*|units?|pieces?|pcs?|items?)",
                    RegexOptions.IgnoreCase);

                if (!condMatch.Success)
                {
                    continue;
                }

                if (!int.TryParse(condMatch.Groups["thresh"].Value, out var threshold) || threshold <= 0)
                {
                    continue;
                }

                var pctMatch = Regex.Match(fullRowText, @"\b(?<pct>\d+(?:[\.,]\d+)?)\s*%", RegexOptions.IgnoreCase);
                if (!pctMatch.Success)
                {
                    continue;
                }

                if (decimal.TryParse(pctMatch.Groups["pct"].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var pct) && pct > 0m && pct < 100m)
                {
                    if (quantity >= threshold)
                    {
                        applicableRules.Add((threshold, pct));
                    }
                }
            }
        }

        if (applicableRules.Count == 0)
        {
            return null;
        }

        if (applicableRules.Count == 1)
        {
            return applicableRules[0].Percent;
        }

        var maxThreshold = applicableRules.Max(r => r.Threshold);
        var bestRules = applicableRules.Where(r => r.Threshold == maxThreshold).ToList();
        if (bestRules.Count == 1)
        {
            return bestRules[0].Percent;
        }

        return null;
    }

    private static (bool IsValid, string? Reason) VerifyClaimedDiscount(
        string answer,
        string modelId,
        decimal expectedSubtotal,
        decimal expectedDiscountPercent,
        decimal expectedDiscountAmount,
        decimal expectedFinal)
    {
        // 1. Check stated discount percentage in answer
        foreach (Match m in ClaimedDiscountPercentRegex().Matches(answer))
        {
            var pctStr = m.Groups["pct"].Value.Replace(',', '.');
            if (decimal.TryParse(pctStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var claimedPct))
            {
                if (claimedPct != expectedDiscountPercent)
                {
                    return (false, $"Заявленный процент скидки {claimedPct:0.##}% не соответствует условиям в документе ({expectedDiscountPercent:0.##}%)");
                }
            }
        }

        // 2. Check discount multiplication formula: "98000 × (1 - 0.07) = 91260" or "98000 × 0.93 = 91140"
        foreach (Match m in DiscountMultiplicationRegex().Matches(answer))
        {
            if (TryParsePriceDecimal(m.Groups["subtotal"].Value, out var sub) && sub == expectedSubtotal &&
                TryParsePriceDecimal(m.Groups["res"].Value, out var res))
            {
                if (m.Groups["dec"].Success)
                {
                    var decStr = m.Groups["dec"].Value.Replace(',', '.');
                    if (decimal.TryParse(decStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var dec))
                    {
                        var usedPct = dec * 100m;
                        if (usedPct != expectedDiscountPercent)
                        {
                            return (false, $"В расчёте скидки использован коэффициент, соответствующий {usedPct:0.##}%, но в документе указана скидка {expectedDiscountPercent:0.##}%");
                        }
                    }
                }
                else if (m.Groups["mult"].Success)
                {
                    var multStr = m.Groups["mult"].Value.Replace(',', '.');
                    if (decimal.TryParse(multStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var mult) && mult > 0m && mult < 1m)
                    {
                        var usedPct = (1m - mult) * 100m;
                        if (Math.Abs(usedPct - expectedDiscountPercent) > 0.01m)
                        {
                            return (false, $"В расчёте скидки использован коэффициент, соответствующий {usedPct:0.##}%, но в документе указана скидка {expectedDiscountPercent:0.##}%");
                        }
                    }
                }

                if (res != expectedFinal)
                {
                    return (false, $"Заявленная стоимость после скидки {res:0.##} не соответствует расчёту {expectedSubtotal:0.##} с применением скидки {expectedDiscountPercent:0.##}%: ожидается {expectedFinal:0.##}");
                }

                return (true, null);
            }
        }

        // 3. Check discount subtraction formula: "98000 - 6860 = 91140"
        foreach (Match m in DiscountSubtractionRegex().Matches(answer))
        {
            if (TryParsePriceDecimal(m.Groups["subtotal"].Value, out var sub) && sub == expectedSubtotal &&
                TryParsePriceDecimal(m.Groups["discount"].Value, out var disc) &&
                TryParsePriceDecimal(m.Groups["res"].Value, out var res))
            {
                if (disc != expectedDiscountAmount)
                {
                    return (false, $"Заявленный размер скидки {disc:0.##} не соответствует расчёту {expectedSubtotal:0.##} × {expectedDiscountPercent:0.##}% = {expectedDiscountAmount:0.##}");
                }

                if (res != expectedFinal)
                {
                    return (false, $"Заявленная стоимость после скидки {res:0.##} не соответствует расчёту {expectedSubtotal:0.##} с применением скидки {expectedDiscountPercent:0.##}%: ожидается {expectedFinal:0.##}");
                }

                return (true, null);
            }
        }

        // 4. Check explicit final total phrase: "После скидки: 91140 руб.", "Стоимость после скидки: 91140 руб."
        var phraseMatch = ClaimedFinalTotalPhraseRegex().Match(answer);
        if (phraseMatch.Success)
        {
            var rawVal = phraseMatch.Groups["val"].Value;
            if (TryParsePriceDecimal(rawVal, out var phraseAmount))
            {
                if (phraseAmount != expectedFinal)
                {
                    return (false, $"Заявленная стоимость после скидки {phraseAmount:0.##} не соответствует расчёту {expectedSubtotal:0.##} с применением скидки {expectedDiscountPercent:0.##}%: ожидается {expectedFinal:0.##}");
                }

                return (true, null);
            }
        }

        return (true, null);
    }

    private static (bool IsValid, string? Reason) ValidateStockAvailability(
        string answer,
        IReadOnlyList<ParsedTable> tables,
        string? question)
    {
        if (string.IsNullOrWhiteSpace(question) || !IsAvailabilityQuestion(question))
        {
            return (true, null);
        }

        var requestedModels = FindRequestedModelIdentifiers(question);
        if (requestedModels.Count == 0)
        {
            return (true, null);
        }

        foreach (var modelId in requestedModels)
        {
            decimal? resolvedStock = null;
            foreach (var table in tables)
            {
                if (TryResolveStockQuantity(table, modelId, out var stock))
                {
                    resolvedStock = stock;
                    break;
                }
            }

            if (resolvedStock == null)
            {
                continue;
            }

            var stockQuantity = resolvedStock.Value;
            var modelContext = ExtractModelContext(answer, modelId);
            var semantics = ClassifyStockAnswer(modelContext);

            if (semantics == StockAnswerSemantics.Unresolved)
            {
                continue;
            }

            if (stockQuantity <= 0m)
            {
                if (semantics == StockAnswerSemantics.Positive)
                {
                    return (false, $"Модель '{modelId}' отсутствует на складе (остаток 0), но в ответе утверждается о её наличии");
                }

                if (semantics == StockAnswerSemantics.Unknown)
                {
                    return (false, $"В цитируемых источниках указан остаток модели '{modelId}' (остаток 0, нет в наличии), но в ответе утверждается об отсутствии информации");
                }
            }
            else // stockQuantity > 0m
            {
                if (semantics == StockAnswerSemantics.Negative)
                {
                    return (false, $"Модель '{modelId}' имеется в наличии на складе (остаток {stockQuantity:0.##}), но в ответе утверждается о её отсутствии");
                }

                if (semantics == StockAnswerSemantics.Unknown)
                {
                    return (false, $"В цитируемых источниках указан остаток модели '{modelId}' (остаток {stockQuantity:0.##}, в наличии), но в ответе утверждается об отсутствии информации");
                }
            }
        }

        return (true, null);
    }

    private static bool IsAvailabilityQuestion(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return false;
        }

        if (IsExtremumQuestion(question, out _) || IsCalculationQuestion(question))
        {
            return false;
        }

        return AvailabilityQuestionRegex().IsMatch(question);
    }

    private static bool IsTotalStockHeader(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        if (Regex.IsMatch(header, @"\b(?:цен\w*|стоимост\w*|сум\w*|руб\w*|канал\w*|вход\w*|выход\w*|заказ\w*|продаж\w*)\b", RegexOptions.IgnoreCase))
        {
            return false;
        }

        return TotalStockHeaderRegex().IsMatch(header.Trim());
    }

    private static bool IsStockHeader(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        if (Regex.IsMatch(header, @"\b(?:цен\w*|стоимост\w*|сум\w*|руб\w*|канал\w*|вход\w*|выход\w*|заказ\w*|продаж\w*)\b", RegexOptions.IgnoreCase))
        {
            return false;
        }

        return StockHeaderRegex().IsMatch(header);
    }

    internal static int ResolveStockColumn(ParsedTable table)
    {
        if (table.Headers == null || table.Headers.Count == 0)
        {
            return -1;
        }

        // 1. Total stock column ("Всего", "Всего, шт.", "Total", "Итого")
        for (var i = 0; i < table.Headers.Count; i++)
        {
            if (IsTotalStockHeader(table.Headers[i]))
            {
                return i;
            }
        }

        // 2. Specific stock column ("Остаток", "Остаток, шт.", "Stock", "Quantity", "Qty")
        var stockIndices = new List<int>();
        for (var i = 0; i < table.Headers.Count; i++)
        {
            if (IsStockHeader(table.Headers[i]))
            {
                stockIndices.Add(i);
            }
        }

        if (stockIndices.Count == 1)
        {
            return stockIndices[0];
        }

        return -1;
    }

    internal static bool TryResolveStockQuantity(
        ParsedTable table,
        string modelId,
        out decimal quantity)
    {
        quantity = 0m;
        var stockCol = ResolveStockColumn(table);
        if (stockCol < 0)
        {
            return false;
        }

        foreach (var row in table.Rows)
        {
            if (RowMatchesModel(row, modelId))
            {
                if (stockCol < row.Cells.Count && TryParseStockDecimal(row.Cells[stockCol], out quantity))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryParseStockDecimal(string text, out decimal stock)
    {
        stock = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = Regex.Replace(text.Trim(), @"(?<=\d)\s+(?=\d)", "");
        var m = Regex.Match(cleaned, @"\b\d+(?:[\.,]\d{1,2})?\b");
        if (!m.Success)
        {
            return false;
        }

        var numStr = m.Value.Replace(',', '.');
        if (decimal.TryParse(numStr, NumberStyles.Number, CultureInfo.InvariantCulture, out stock) && stock >= 0m)
        {
            return true;
        }

        return false;
    }

    private static StockAnswerSemantics ClassifyStockAnswer(string answer)
    {
        if (StockUnknownAnswerRegex().IsMatch(answer))
        {
            return StockAnswerSemantics.Unknown;
        }

        if (StockNegativeAnswerRegex().IsMatch(answer))
        {
            return StockAnswerSemantics.Negative;
        }

        if (StockPositiveAnswerRegex().IsMatch(answer))
        {
            return StockAnswerSemantics.Positive;
        }

        return StockAnswerSemantics.Unresolved;
    }

    private static string ExtractModelContext(string answer, string modelId)
    {
        var sentences = answer.Split(['.', ';', '!', '?', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        var matching = new List<string>();
        foreach (var s in sentences)
        {
            if (ContainsModelIdentifier(s, modelId))
            {
                matching.Add(s);
            }
        }

        if (matching.Count > 0)
        {
            return string.Join(". ", matching);
        }

        return answer;
    }

    private enum StockAnswerSemantics
    {
        Unresolved,
        Unknown,
        Negative,
        Positive
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
