using System.Text.RegularExpressions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;

namespace Pigmentor.Retrieval;

/// <summary>
/// Deterministic query-scope resolver. Maps explicit user intent to storage-neutral <see cref="MetadataFilter"/>
/// across existing silos (e.g. product-catalog, technical-docs) without modifying the original question.
/// </summary>
public static partial class QueryScopeResolver
{
    // Explicit product-catalog expressions:
    // e.g. "из каталога", "в каталоге", "по каталогу", "товары из каталога", "товар из каталога"
    [GeneratedRegex(@"\b(?:из|в|по)\s+каталог\w*\b|\bкаталог\w*\s+товар\w*\b|\bтовар\w*\s+(?:из|в|по)\s+каталог\w*\b", RegexOptions.IgnoreCase)]
    private static partial Regex CatalogExplicitScopeRegex();

    // Catalog item queries with catalog-specific attributes:
    // e.g. "Какой товар ... имеет наибольший гарантийный срок", "товар ... гарантия", "товар ... прайс"
    [GeneratedRegex(@"\bтовар\w*\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProductWordRegex();

    [GeneratedRegex(@"\b(?:гаранти\w*|прайс\w*|артикул\w*|остат\w*|склад\w*|скидк\w*|стоимост\w*|цен\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CatalogAttributeRegex();

    // Discount queries:
    // e.g. "скидка", "скидки", "скидку"
    [GeneratedRegex(@"\bскидк\w*\b", RegexOptions.IgnoreCase)]
    private static partial Regex DiscountScopeRegex();

    // Known product-catalog models with commercial intent:
    [GeneratedRegex(@"\b(?:TM-100|TM-200|PR-10|PR-25|LV-8|AI-8X)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CatalogModelRegex();

    [GeneratedRegex(@"\b(?:цена|цен\w*|стоимост\w*|скидк\w*|заказ\w*|купить|купи\w*|наличи\w*|стоит)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CommercialTermsRegex();

    // Warehouse/stock queries:
    // The warehouse noun itself is catalog-specific, including constructions such as
    // "на каком складе", where a preposition is not adjacent to the noun.
    [GeneratedRegex(@"\bсклад\w*\b", RegexOptions.IgnoreCase)]
    private static partial Regex WarehouseScopeRegex();

    /// <summary>
    /// Resolves explicit silo scope from the query text, or returns null if the query is generic.
    /// </summary>
    public static MetadataFilter? ResolveScope(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        // 1. Explicit product-catalog scope
        if (CatalogExplicitScopeRegex().IsMatch(query) ||
            WarehouseScopeRegex().IsMatch(query) ||
            DiscountScopeRegex().IsMatch(query) ||
            (ProductWordRegex().IsMatch(query) && CatalogAttributeRegex().IsMatch(query)) ||
            (CatalogModelRegex().IsMatch(query) && CommercialTermsRegex().IsMatch(query)))
        {
            return MetadataFilter.Where(m => m.Silo == "product-catalog");
        }

        return null;
    }
}
