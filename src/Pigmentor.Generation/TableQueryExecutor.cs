using System.Globalization;
using System.Text.RegularExpressions;

namespace Pigmentor.Generation;

internal static partial class TableQueryExecutor
{
    public static bool TryExecute(string question, RagContext context, out string answer)
    {
        answer = string.Empty;
        var tables = ParseTables(context);
        if (tables.Count == 0)
        {
            return false;
        }

        return TryExecuteRangeEnumeration(question, tables, out answer)
            || TryExecuteTerminalLookup(question, tables, out answer)
            || TryExecuteWarehouseSum(question, tables, out answer)
            || TryExecutePriceFilter(question, tables, out answer)
            || TryExecuteWarrantyFilter(question, tables, out answer)
            || TryExecutePriceCalculation(question, tables, out answer);
    }

    private static bool TryExecuteRangeEnumeration(string question, IReadOnlyList<TableData> tables, out string answer)
    {
        answer = string.Empty;
        if (!RangeIntentRegex().IsMatch(question))
        {
            return false;
        }

        var category = CurrentIntentRegex().IsMatch(question) ? "Ток"
            : VoltageIntentRegex().IsMatch(question) ? "Напряжение"
            : null;
        if (category == null)
        {
            return false;
        }

        foreach (var table in tables)
        {
            var rows = table.Rows
                .Where(row => row.Count >= 3 && row[0].Equals(category, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            answer = string.Join("\n", rows.Select(row =>
                $"- {row[1].Trim('(', ')')} {row[2]}{(row.Count > 3 && !string.IsNullOrWhiteSpace(row[3]) ? $", погрешность {row[3]}" : string.Empty)} [{table.SourceLabel}]"));
            return true;
        }
        return false;
    }

    private static bool TryExecuteTerminalLookup(string question, IReadOnlyList<TableData> tables, out string answer)
    {
        answer = string.Empty;
        if (!TerminalIntentRegex().IsMatch(question))
        {
            return false;
        }
        var channel = ResolveOrdinal(question);
        if (channel == null)
        {
            return false;
        }

        foreach (var table in tables)
        {
            var terminals = table.Rows
                .Where(row => row.Any(cell => Regex.IsMatch(cell, $@"\bAI\s*{channel}\s*[+-](?!\d)", RegexOptions.IgnoreCase)))
                .SelectMany(row => row.Where(cell => TerminalRegex().IsMatch(cell)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (terminals.Count < 2)
            {
                continue;
            }

            answer = $"Для входного канала {channel} используются контакты {string.Join(" и ", terminals)} [{table.SourceLabel}].";
            return true;
        }
        return false;
    }

    private static bool TryExecuteWarehouseSum(string question, IReadOnlyList<TableData> tables, out string answer)
    {
        answer = string.Empty;
        if (!WarehouseAggregateIntentRegex().IsMatch(question))
        {
            return false;
        }

        foreach (var table in tables.Where(table => table.Headers != null))
        {
            var cityIndex = Enumerable.Range(0, table.Headers!.Count)
                .FirstOrDefault(i => ContainsHeaderReference(question, table.Headers[i]), -1);
            if (cityIndex <= 0)
            {
                continue;
            }

            var values = table.Rows
                .Where(row => cityIndex < row.Count)
                .Select(row => ParseDecimal(row[cityIndex]))
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToList();
            if (values.Count < 2)
            {
                continue;
            }

            answer = $"Суммарное количество в {table.Headers[cityIndex]} составляет {values.Sum():0.##} единиц [{table.SourceLabel}].";
            return true;
        }
        return false;
    }

    private static bool TryExecutePriceFilter(string question, IReadOnlyList<TableData> tables, out string answer)
    {
        answer = string.Empty;
        var match = PriceFilterRegex().Match(question);
        if (!match.Success || !decimal.TryParse(match.Groups["value"].Value.Replace(" ", ""), out var threshold))
        {
            return false;
        }
        var cheaper = match.Groups["op"].Value.Equals("дешевле", StringComparison.OrdinalIgnoreCase);

        foreach (var table in tables.Where(table => table.Headers != null))
        {
            var modelIndex = FindHeader(table, "модель");
            var priceIndex = FindHeader(table, "цена");
            if (modelIndex < 0 || priceIndex < 0)
            {
                continue;
            }
            var models = table.Rows
                .Where(row => modelIndex < row.Count && priceIndex < row.Count)
                .Select(row => (Model: row[modelIndex], Price: ParseDecimal(row[priceIndex])))
                .Where(item => item.Price.HasValue && (cheaper ? item.Price < threshold : item.Price > threshold))
                .Select(item => item.Model)
                .ToList();
            if (models.Count == 0)
            {
                continue;
            }
            answer = $"Модели {match.Groups["op"].Value} {threshold:0.##} руб.: {string.Join(", ", models)} [{table.SourceLabel}].";
            return true;
        }
        return false;
    }

    private static bool TryExecuteWarrantyFilter(string question, IReadOnlyList<TableData> tables, out string answer)
    {
        answer = string.Empty;
        var match = WarrantyFilterRegex().Match(question);
        if (!match.Success || !decimal.TryParse(match.Groups["value"].Value, out var expected))
        {
            return false;
        }
        foreach (var table in tables.Where(table => table.Headers != null))
        {
            var modelIndex = FindHeader(table, "модель");
            var warrantyIndex = FindHeader(table, "гарантия");
            if (modelIndex < 0 || warrantyIndex < 0)
            {
                continue;
            }
            var models = table.Rows
                .Where(row => modelIndex < row.Count && warrantyIndex < row.Count && ParseDecimal(row[warrantyIndex]) == expected)
                .Select(row => row[modelIndex])
                .ToList();
            if (models.Count == 0)
            {
                continue;
            }
            answer = $"Гарантию {expected:0.##} месяцев имеют: {string.Join(", ", models)} [{table.SourceLabel}].";
            return true;
        }
        return false;
    }

    private static bool TryExecutePriceCalculation(string question, IReadOnlyList<TableData> tables, out string answer)
    {
        answer = string.Empty;
        var modelMatch = ModelRegex().Match(question);
        var quantity = ResolveQuantity(question);
        if (!CalculationIntentRegex().IsMatch(question) || quantity == null || !modelMatch.Success)
        {
            return false;
        }
        var model = modelMatch.Value;
        decimal? unitPrice = null;
        string? priceSource = null;
        foreach (var table in tables.Where(table => table.Headers != null))
        {
            var modelIndex = FindHeader(table, "модель");
            var priceIndex = FindHeader(table, "цена");
            var row = table.Rows.FirstOrDefault(row => modelIndex >= 0 && priceIndex >= 0 && modelIndex < row.Count &&
                row[modelIndex].Equals(model, StringComparison.OrdinalIgnoreCase));
            if (row != null && priceIndex < row.Count)
            {
                unitPrice = ParseDecimal(row[priceIndex]);
                priceSource = table.SourceLabel;
                break;
            }
        }
        if (unitPrice == null || priceSource == null)
        {
            return false;
        }

        decimal discount = 0;
        string? discountSource = null;
        foreach (var table in tables)
        {
            foreach (var row in table.Rows)
            {
                var text = string.Join(" ", row);
                var rule = DiscountRuleRegex().Match(text);
                if (rule.Success && int.TryParse(rule.Groups["quantity"].Value, out var minimum) && quantity.Value >= minimum &&
                    decimal.TryParse(rule.Groups["percent"].Value, out var percent) && percent >= discount)
                {
                    discount = percent;
                    discountSource = table.SourceLabel;
                }
            }
        }

        var subtotal = unitPrice.Value * quantity.Value;
        var total = decimal.Round(subtotal * (1m - discount / 100m), 2, MidpointRounding.AwayFromZero);
        answer = $"Стоимость до скидки: {unitPrice:0.##} × {quantity.Value} = {subtotal:0.##} руб. [{priceSource}]. " +
                 (discount > 0
                     ? $"Скидка {discount:0.##} %, итог после скидки: {total:0.##} руб. [{discountSource}]."
                     : $"Скидка за количество не применяется, итог: {total:0.##} руб. [{priceSource}].");
        return true;
    }

    private static List<TableData> ParseTables(RagContext context)
    {
        var tables = new List<TableData>();
        foreach (var (label, source) in context.SourceMap)
        {
            var lines = source.Chunk.Text.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!IsTableLine(lines[i])) continue;
                var block = new List<string>();
                while (i < lines.Length && IsTableLine(lines[i])) block.Add(lines[i++]);
                i--;
                var parsedRows = block.Where(line => !IsSeparator(line)).Select(ParseRow).Where(row => row.Count > 0).ToList();
                if (parsedRows.Count == 0) continue;
                var firstLooksLikeData = parsedRows[0][0].Equals("Ток", StringComparison.OrdinalIgnoreCase) ||
                                         parsedRows[0][0].Equals("Напряжение", StringComparison.OrdinalIgnoreCase);
                IReadOnlyList<string>? headers = firstLooksLikeData ? null : parsedRows[0];
                var rows = firstLooksLikeData ? parsedRows : parsedRows.Skip(1).ToList();
                if (rows.Count > 0) tables.Add(new TableData(label, headers, rows));
            }
        }
        return tables;
    }

    private static bool IsTableLine(string line) => line.TrimStart().StartsWith('|') && line.TrimEnd().EndsWith('|');
    private static bool IsSeparator(string line) => Regex.IsMatch(line, @"^\s*\|(?:\s*:?-{3,}:?\s*\|)+\s*$");
    private static List<string> ParseRow(string line) => line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToList();
    private static int FindHeader(TableData table, string stem) => table.Headers == null ? -1 :
        Enumerable.Range(0, table.Headers.Count).FirstOrDefault(i => table.Headers[i].Contains(stem, StringComparison.OrdinalIgnoreCase), -1);
    private static decimal? ParseDecimal(string text) => decimal.TryParse(Regex.Replace(text, @"[^0-9,.-]", "").Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static bool ContainsWord(string text, string word) => Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(word) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase);
    private static bool ContainsHeaderReference(string text, string header)
    {
        if (ContainsWord(text, header)) return true;
        if (header.Length < 5) return false;
        var stem = header[..Math.Max(4, header.Length - 2)];
        return Regex.IsMatch(text, @"\b" + Regex.Escape(stem) + @"\p{L}{0,3}\b", RegexOptions.IgnoreCase);
    }

    private static int? ResolveOrdinal(string text)
    {
        var digit = Regex.Match(text, @"\b(?<n>[1-8])\b");
        if (digit.Success) return int.Parse(digit.Groups["n"].Value, CultureInfo.InvariantCulture);
        string[] stems = ["перв", "втор", "трет", "четв", "пят", "шест", "седьм", "восьм"];
        for (var i = 0; i < stems.Length; i++) if (text.Contains(stems[i], StringComparison.OrdinalIgnoreCase)) return i + 1;
        return null;
    }

    private static int? ResolveQuantity(string text)
    {
        var digit = QuantityRegex().Match(text);
        if (digit.Success && int.TryParse(digit.Groups["quantity"].Value, out var quantity))
        {
            return quantity;
        }

        string[] stems = ["одн", "дв", "тр", "четыр", "пят", "шест", "сем", "восем", "девят", "десят"];
        for (var i = 0; i < stems.Length; i++)
        {
            if (Regex.IsMatch(text, @"\b" + stems[i] + @"\p{L}*\s+(?:единиц\w*|шт\.?|датчик\w*|прибор\w*|модул\w*|[A-Z]{2,}(?:-[A-Z0-9]+)+)", RegexOptions.IgnoreCase))
            {
                return i + 1;
            }
        }
        return null;
    }

    private sealed record TableData(string SourceLabel, IReadOnlyList<string>? Headers, IReadOnlyList<List<string>> Rows);

    [GeneratedRegex(@"\bдиапазон\w*\b", RegexOptions.IgnoreCase)] private static partial Regex RangeIntentRegex();
    [GeneratedRegex(@"\bток\w*\b|\bтоков\w*\b", RegexOptions.IgnoreCase)] private static partial Regex CurrentIntentRegex();
    [GeneratedRegex(@"\bнапряжени\w*\b", RegexOptions.IgnoreCase)] private static partial Regex VoltageIntentRegex();
    [GeneratedRegex(@"\b(?:клемм\w*|контакт\w*|вывод\w*|пин\w*)\b[^?\r\n]*\b(?:вход\w*|канал\w*)\b|\b(?:вход\w*|канал\w*)\b[^?\r\n]*\b(?:клемм\w*|контакт\w*|вывод\w*|пин\w*)\b", RegexOptions.IgnoreCase)] private static partial Regex TerminalIntentRegex();
    [GeneratedRegex(@"\bX1:\d+\b", RegexOptions.IgnoreCase)] private static partial Regex TerminalRegex();
    [GeneratedRegex(@"\b(?:суммарно|сумма|всего|всех\s+(?:модел\w*|товар\w*|оборудовани\w*))\b", RegexOptions.IgnoreCase)] private static partial Regex WarehouseAggregateIntentRegex();
    [GeneratedRegex(@"\b(?<op>дешевле|дороже)\s+(?<value>\d[\d\s]*)\s*(?:руб|рубл)", RegexOptions.IgnoreCase)] private static partial Regex PriceFilterRegex();
    [GeneratedRegex(@"\bгаранти\w*\s+(?<value>\d+)\s*месяц", RegexOptions.IgnoreCase)] private static partial Regex WarrantyFilterRegex();
    [GeneratedRegex(@"\b(?<quantity>\d+)\s+(?:единиц\w*|шт\.?|датчик\w*|прибор\w*|модул\w*)", RegexOptions.IgnoreCase)] private static partial Regex QuantityRegex();
    [GeneratedRegex(@"\b[A-Z]{2,}(?:-[A-Z0-9]+)+\b", RegexOptions.IgnoreCase)] private static partial Regex ModelRegex();
    [GeneratedRegex(@"\b(?:стоимост\w*|стоят\w*|рассчит\w*|цена|цену)\b", RegexOptions.IgnoreCase)] private static partial Regex CalculationIntentRegex();
    [GeneratedRegex(@"(?:от\s+)?(?<quantity>\d+)\s+единиц[^|\r\n]*?(?<percent>\d+(?:[\.,]\d+)?)\s*%", RegexOptions.IgnoreCase)] private static partial Regex DiscountRuleRegex();
}
