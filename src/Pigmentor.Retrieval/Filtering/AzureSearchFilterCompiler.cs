using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;

namespace Pigmentor.Retrieval.Filtering;

/// <summary>
/// Best-effort translator from a <see cref="MetadataFilter"/> to an Azure AI
/// Search OData <c>$filter</c> string over the filterable fields declared in
/// <c>AzureAiSearchVectorStore.BuildIndex</c>. Returns <see langword="null"/>
/// when the expression uses a node this translator does not cover, signaling
/// the adapter to fall back to in-process post-filtering.
/// </summary>
/// <remarks>
/// Covers the same operator family as the Qdrant compiler that is practical in
/// OData: <c>eq</c>/<c>ne</c>, <c>and</c>/<c>or</c>, range comparisons
/// (<c>lt</c>/<c>le</c>/<c>gt</c>/<c>ge</c>), and null checks (<c>eq null</c> /
/// <c>ne null</c>). Set membership (<c>Contains</c>) is left to the post-filter
/// fallback because it needs Azure's <c>search.in</c> collection semantics that
/// the scalar fields here do not model cleanly.
/// </remarks>
internal static class AzureSearchFilterCompiler
{
    /// <summary>Compile to an OData filter string, or null when unsupported.</summary>
    public static string? TryCompile(MetadataFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        try
        {
            return Visit(filter.Predicate.Body, filter.Predicate.Parameters[0]);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string Visit(Expression node, ParameterExpression param) => node.NodeType switch
    {
        ExpressionType.AndAlso => Combine((BinaryExpression)node, "and", param),
        ExpressionType.OrElse => Combine((BinaryExpression)node, "or", param),
        ExpressionType.Equal => Comparison((BinaryExpression)node, "eq", param),
        ExpressionType.NotEqual => Comparison((BinaryExpression)node, "ne", param),
        ExpressionType.LessThan => Comparison((BinaryExpression)node, "lt", param),
        ExpressionType.LessThanOrEqual => Comparison((BinaryExpression)node, "le", param),
        ExpressionType.GreaterThan => Comparison((BinaryExpression)node, "gt", param),
        ExpressionType.GreaterThanOrEqual => Comparison((BinaryExpression)node, "ge", param),
        ExpressionType.Constant when node is ConstantExpression { Value: bool b } => b ? "true" : "false",
        _ => throw new NotSupportedException($"Unsupported OData node {node.NodeType}."),
    };

    private static string Combine(BinaryExpression bin, string op, ParameterExpression param)
        => $"({Visit(bin.Left, param)} {op} {Visit(bin.Right, param)})";

    private static string Comparison(BinaryExpression bin, string op, ParameterExpression param)
    {
        // member <op> value
        if (TryMember(bin.Left, param, out var field) && TryValue(bin.Right, out var value))
        {
            return $"{field} {op} {Literal(value)}";
        }
        // value <op> member -> flip the operator so the member stays on the left.
        if (TryMember(bin.Right, param, out field) && TryValue(bin.Left, out value))
        {
            return $"{field} {FlipOp(op)} {Literal(value)}";
        }
        throw new NotSupportedException($"Unsupported OData comparison {bin}.");
    }

    private static string FlipOp(string op) => op switch
    {
        "lt" => "gt",
        "le" => "ge",
        "gt" => "lt",
        "ge" => "le",
        _ => op,
    };

    private static string Literal(object? value) => value switch
    {
        null => "null",
        string s => $"'{s.Replace("'", "''", StringComparison.Ordinal)}'",
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
    };

    private static bool TryMember(Expression expr, ParameterExpression param, out string field)
    {
        if (expr is UnaryExpression { NodeType: ExpressionType.Convert } u)
        {
            expr = u.Operand;
        }
        if (expr is MemberExpression me && me.Expression == param)
        {
            field = ToFieldName(me.Member.Name);
            return true;
        }
        field = "";
        return false;
    }

    private static bool TryValue(Expression expr, out object? value)
    {
        if (expr is UnaryExpression { NodeType: ExpressionType.Convert } u)
        {
            expr = u.Operand;
        }
        if (expr is ConstantExpression ce)
        {
            value = ce.Value;
            return true;
        }
        if (expr is MemberExpression me && me.Expression is ConstantExpression target)
        {
            value = me.Member switch
            {
                System.Reflection.FieldInfo f => f.GetValue(target.Value),
                System.Reflection.PropertyInfo p => p.GetValue(target.Value),
                _ => null,
            };
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>Map a CLR property name to the Azure index field name (see BuildIndex).</summary>
    private static string ToFieldName(string property) => property switch
    {
        nameof(DocumentMetadata.Department) => "department",
        nameof(DocumentMetadata.Office) => "office",
        nameof(DocumentMetadata.ConfidentialityLevel) => "confidentiality",
        nameof(DocumentMetadata.DocumentType) => "document_type",
        nameof(DocumentMetadata.FiscalYear) => "fiscal_year",
        nameof(DocumentMetadata.Author) => "author",
        nameof(DocumentMetadata.Silo) => "silo",
        nameof(DocumentMetadata.Title) => "title",
        nameof(DocumentMetadata.Id) => "document_id",
        _ => ToSnakeCase(property),
    };

    private static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                sb.Append('_');
            }
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}
