using System.Linq.Expressions;
using System.Reflection;
using Pigmentor.Core.Documents;

namespace Pigmentor.Core.Filtering;

/// <summary>
/// Parses LINQ expressions over <see cref="DocumentMetadata"/> into a structured <see cref="FilterNode"/> AST.
/// </summary>
internal static class FilterExpressionParser
{
    public static FilterNode Parse(Expression<Func<DocumentMetadata, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var parameter = predicate.Parameters[0];
        return ParseNode(predicate.Body, parameter);
    }

    private static FilterNode ParseNode(Expression expression, ParameterExpression parameter)
    {
        // Unwrap redundant conversions
        if (expression is UnaryExpression { NodeType: ExpressionType.Convert } convert &&
            convert.Type == typeof(bool))
        {
            expression = convert.Operand;
        }

        switch (expression.NodeType)
        {
            case ExpressionType.AndAlso:
                {
                    var bin = (BinaryExpression)expression;
                    return new LogicalFilterNode(
                        LogicalOperator.And,
                        ParseNode(bin.Left, parameter),
                        ParseNode(bin.Right, parameter));
                }

            case ExpressionType.OrElse:
                {
                    var bin = (BinaryExpression)expression;
                    return new LogicalFilterNode(
                        LogicalOperator.Or,
                        ParseNode(bin.Left, parameter),
                        ParseNode(bin.Right, parameter));
                }

            case ExpressionType.Not:
                {
                    var unary = (UnaryExpression)expression;
                    return new LogicalFilterNode(LogicalOperator.Not, ParseNode(unary.Operand, parameter));
                }

            case ExpressionType.Constant:
                {
                    var constant = (ConstantExpression)expression;
                    if (constant.Value is bool b)
                    {
                        return new ConstantFilterNode(b);
                    }

                    throw new NotSupportedException($"Unexpected constant filter expression of type '{constant.Type.Name}'.");
                }

            case ExpressionType.Equal:
            case ExpressionType.NotEqual:
            case ExpressionType.GreaterThan:
            case ExpressionType.GreaterThanOrEqual:
            case ExpressionType.LessThan:
            case ExpressionType.LessThanOrEqual:
                {
                    var bin = (BinaryExpression)expression;
                    return ParseComparison(bin, parameter);
                }

            default:
                throw new NotSupportedException($"Filter expression '{expression}' of node type '{expression.NodeType}' is not supported.");
        }
    }

    private static ComparisonFilterNode ParseComparison(BinaryExpression bin, ParameterExpression parameter)
    {
        if (TryGetProperty(bin.Left, parameter, out var propLeft))
        {
            var value = EvaluateValue(bin.Right);
            var op = MapBinaryOperator(bin.NodeType, inverted: false);
            return new ComparisonFilterNode(propLeft.Name, op, value);
        }

        if (TryGetProperty(bin.Right, parameter, out var propRight))
        {
            var value = EvaluateValue(bin.Left);
            var op = MapBinaryOperator(bin.NodeType, inverted: true);
            return new ComparisonFilterNode(propRight.Name, op, value);
        }

        throw new NotSupportedException($"Comparison '{bin}' must compare a DocumentMetadata property with a constant or value expression.");
    }

    private static bool TryGetProperty(Expression expr, ParameterExpression parameter, out PropertyInfo property)
    {
        if (expr is UnaryExpression { NodeType: ExpressionType.Convert } convert)
        {
            expr = convert.Operand;
        }

        if (expr is MemberExpression member &&
            member.Expression == parameter &&
            member.Member is PropertyInfo prop &&
            (prop.DeclaringType == typeof(DocumentMetadata) || typeof(DocumentMetadata).IsAssignableFrom(prop.DeclaringType)))
        {
            property = prop;
            return true;
        }

        property = null!;
        return false;
    }

    private static object? EvaluateValue(Expression expr)
    {
        if (expr is UnaryExpression { NodeType: ExpressionType.Convert } convert)
        {
            expr = convert.Operand;
        }

        if (expr is ConstantExpression constant)
        {
            return constant.Value;
        }

        var boxed = Expression.Convert(expr, typeof(object));
        var lambda = Expression.Lambda<Func<object?>>(boxed);
        return lambda.Compile()();
    }

    private static ComparisonOperator MapBinaryOperator(ExpressionType nodeType, bool inverted)
    {
        return (nodeType, inverted) switch
        {
            (ExpressionType.Equal, _) => ComparisonOperator.Equal,
            (ExpressionType.NotEqual, _) => ComparisonOperator.NotEqual,

            (ExpressionType.GreaterThan, false) => ComparisonOperator.GreaterThan,
            (ExpressionType.GreaterThan, true) => ComparisonOperator.LessThan,

            (ExpressionType.GreaterThanOrEqual, false) => ComparisonOperator.GreaterThanOrEqual,
            (ExpressionType.GreaterThanOrEqual, true) => ComparisonOperator.LessThanOrEqual,

            (ExpressionType.LessThan, false) => ComparisonOperator.LessThan,
            (ExpressionType.LessThan, true) => ComparisonOperator.GreaterThan,

            (ExpressionType.LessThanOrEqual, false) => ComparisonOperator.LessThanOrEqual,
            (ExpressionType.LessThanOrEqual, true) => ComparisonOperator.GreaterThanOrEqual,

            _ => throw new NotSupportedException($"Binary comparison operator '{nodeType}' is not supported.")
        };
    }
}

