using System.Linq.Expressions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Core.Filtering;

/// <summary>
/// Lightweight metadata filter — wraps an <see cref="Expression{TDelegate}"/> over <see cref="DocumentMetadata"/>
/// and a structured <see cref="FilterNode"/> AST so callers can compose filters in C# (<see cref="MetadataFilter.Where"/>)
/// and storage backends (e.g. SQLite + sqlite-vec) can translate them to native queries.
/// </summary>
/// <remarks>
/// Lives in <c>Pigmentor.Core</c> so that the <see cref="Pigmentor.Core.Abstractions.IVectorStore"/> port and every
/// adapter can take a pre-filter without external dependencies.
/// </remarks>
public sealed class MetadataFilter
{
    /// <summary>The underlying predicate expression, used for in-memory evaluation.</summary>
    public Expression<Func<DocumentMetadata, bool>> Predicate { get; }

    /// <summary>The structured AST representation of the filter condition.</summary>
    public FilterNode Root { get; }

    private readonly Func<DocumentMetadata, bool> _compiled;

    public MetadataFilter(Expression<Func<DocumentMetadata, bool>> predicate, FilterNode root)
    {
        Predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        Root = root ?? throw new ArgumentNullException(nameof(root));
        _compiled = predicate.Compile();
    }

    private MetadataFilter(Expression<Func<DocumentMetadata, bool>> predicate)
        : this(predicate, FilterExpressionParser.Parse(predicate))
    {
    }

    /// <summary>Build a filter from a C# predicate expression.</summary>
    public static MetadataFilter Where(Expression<Func<DocumentMetadata, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new MetadataFilter(predicate);
    }

    /// <summary>The pass-everything filter.</summary>
    public static MetadataFilter All { get; } = new(_ => true, new ConstantFilterNode(true));

    /// <summary>Apply the filter in process — used by in-memory evaluation and unit tests.</summary>
    public bool Matches(DocumentMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return _compiled(metadata);
    }

    /// <summary>Logical AND of two filters.</summary>
    public MetadataFilter And(MetadataFilter other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var p = Expression.Parameter(typeof(DocumentMetadata));
        var combined = Expression.AndAlso(
            Expression.Invoke(Predicate, p),
            Expression.Invoke(other.Predicate, p));
        var root = new LogicalFilterNode(LogicalOperator.And, Root, other.Root);
        return new MetadataFilter(Expression.Lambda<Func<DocumentMetadata, bool>>(combined, p), root);
    }

    /// <summary>Logical OR of two filters.</summary>
    public MetadataFilter Or(MetadataFilter other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var p = Expression.Parameter(typeof(DocumentMetadata));
        var combined = Expression.OrElse(
            Expression.Invoke(Predicate, p),
            Expression.Invoke(other.Predicate, p));
        var root = new LogicalFilterNode(LogicalOperator.Or, Root, other.Root);
        return new MetadataFilter(Expression.Lambda<Func<DocumentMetadata, bool>>(combined, p), root);
    }
}
