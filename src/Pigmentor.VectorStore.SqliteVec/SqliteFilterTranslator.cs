using Microsoft.Data.Sqlite;
using Pigmentor.Core.Filtering;

namespace Pigmentor.VectorStore.SqliteVec;

/// <summary>
/// Translates a <see cref="FilterNode"/> AST into parameterized SQL compatible with the SQLite <c>chunks</c> table.
/// </summary>
public static class SqliteFilterTranslator
{
    private static readonly Dictionary<string, string> ColumnMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Id"] = "document_id",
        ["DocumentId"] = "document_id",
        ["Silo"] = "silo",
        ["Department"] = "department",
        ["Office"] = "office",
        ["ConfidentialityLevel"] = "confidentiality_level",
        ["DocumentType"] = "document_type",
        ["FiscalYear"] = "fiscal_year",
        ["Author"] = "author",
        ["LastModified"] = "last_modified",
        ["Title"] = "title"
    };

    /// <summary>
    /// Translates a filter node into a SQL predicate and associated SQLite parameters.
    /// Returns <c>(null, [])</c> if the filter matches everything unconditionally.
    /// </summary>
    public static (string? Sql, IReadOnlyList<SqliteParameter> Parameters) Translate(FilterNode? node)
    {
        if (node is null || node is ConstantFilterNode { Value: true })
        {
            return (null, Array.Empty<SqliteParameter>());
        }

        var parameters = new List<SqliteParameter>();
        var sql = TranslateNode(node, parameters);
        return (sql, parameters);
    }

    private static string TranslateNode(FilterNode node, List<SqliteParameter> parameters)
    {
        return node switch
        {
            ConstantFilterNode { Value: true } => "(1 = 1)",
            ConstantFilterNode { Value: false } => "(1 = 0)",

            ComparisonFilterNode comp => TranslateComparison(comp, parameters),

            LogicalFilterNode logical => TranslateLogical(logical, parameters),

            _ => throw new NotSupportedException($"Filter node of type '{node.GetType().Name}' is not supported by SqliteFilterTranslator.")
        };
    }

    private static string TranslateComparison(ComparisonFilterNode node, List<SqliteParameter> parameters)
    {
        if (!ColumnMap.TryGetValue(node.PropertyName, out var column))
        {
            throw new NotSupportedException($"Metadata property '{node.PropertyName}' is not mapped to any known column in the chunks table.");
        }

        if (node.Value is null)
        {
            return node.Operator switch
            {
                ComparisonOperator.Equal => $"{column} IS NULL",
                ComparisonOperator.NotEqual => $"{column} IS NOT NULL",
                _ => throw new NotSupportedException($"Operator '{node.Operator}' is not supported for null comparisons.")
            };
        }

        var paramName = $"$p{parameters.Count}";
        var dbValue = FormatDbValue(node.Value);
        parameters.Add(new SqliteParameter(paramName, dbValue));

        var op = node.Operator switch
        {
            ComparisonOperator.Equal => "=",
            ComparisonOperator.NotEqual => "<>",
            ComparisonOperator.GreaterThan => ">",
            ComparisonOperator.GreaterThanOrEqual => ">=",
            ComparisonOperator.LessThan => "<",
            ComparisonOperator.LessThanOrEqual => "<=",
            _ => throw new NotSupportedException($"Comparison operator '{node.Operator}' is not supported.")
        };

        return $"{column} {op} {paramName}";
    }

    private static string TranslateLogical(LogicalFilterNode node, List<SqliteParameter> parameters)
    {
        if (node.Children.Count == 0)
        {
            return "(1 = 1)";
        }

        switch (node.Operator)
        {
            case LogicalOperator.And:
            {
                var parts = node.Children.Select(c => TranslateNode(c, parameters));
                return $"({string.Join(" AND ", parts)})";
            }

            case LogicalOperator.Or:
            {
                var parts = node.Children.Select(c => TranslateNode(c, parameters));
                return $"({string.Join(" OR ", parts)})";
            }

            case LogicalOperator.Not:
            {
                if (node.Children.Count != 1)
                {
                    throw new InvalidOperationException("LogicalOperator.Not must have exactly one child node.");
                }

                var childSql = TranslateNode(node.Children[0], parameters);
                return $"(NOT {childSql})";
            }

            default:
                throw new NotSupportedException($"Logical operator '{node.Operator}' is not supported.");
        }
    }

    private static object FormatDbValue(object value)
    {
        return value switch
        {
            DateOnly d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            Enum e => e.ToString(),
            _ => value
        };
    }
}
