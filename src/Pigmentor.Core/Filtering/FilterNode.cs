namespace Pigmentor.Core.Filtering;

/// <summary>
/// Root abstract node for the structured metadata filter AST.
/// </summary>
public abstract record FilterNode;

/// <summary>
/// Supported comparison operators for metadata filter conditions.
/// </summary>
public enum ComparisonOperator
{
    Equal,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual
}

/// <summary>
/// Supported logical operators for combining metadata filter nodes.
/// </summary>
public enum LogicalOperator
{
    And,
    Or,
    Not
}

/// <summary>
/// A comparison between a <see cref="Pigmentor.Core.Documents.DocumentMetadata"/> property and a literal value.
/// </summary>
/// <param name="PropertyName">The name of the metadata property being filtered.</param>
/// <param name="Operator">The comparison operator.</param>
/// <param name="Value">The literal value to compare against.</param>
public sealed record ComparisonFilterNode(
    string PropertyName,
    ComparisonOperator Operator,
    object? Value) : FilterNode;

/// <summary>
/// A logical combination (AND, OR, NOT) of one or more filter nodes.
/// </summary>
public sealed record LogicalFilterNode : FilterNode
{
    public LogicalOperator Operator { get; }
    public IReadOnlyList<FilterNode> Children { get; }

    public LogicalFilterNode(LogicalOperator op, IReadOnlyList<FilterNode> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        Operator = op;
        Children = children;
    }

    public LogicalFilterNode(LogicalOperator op, FilterNode left, FilterNode right)
        : this(op, [left, right])
    {
    }

    public LogicalFilterNode(LogicalOperator op, FilterNode child)
        : this(op, [child])
    {
    }
}

/// <summary>
/// A constant boolean filter node (e.g. true for match-all, false for match-none).
/// </summary>
/// <param name="Value">The constant boolean value.</param>
public sealed record ConstantFilterNode(bool Value) : FilterNode;

