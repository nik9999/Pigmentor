using System.Linq.Expressions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Core.Filtering;

/// <summary>
/// Lightweight metadata filter — wraps an
/// <see cref="Expression{TDelegate}"/> over <see cref="DocumentMetadata"/>
/// so callers can compose filters in C# (<see cref="MetadataFilter.Where"/>)
/// and adapter code can compile them to provider-specific filter languages
/// (Qdrant filter JSON, Azure AI Search OData, pgvector WHERE).
/// </summary>
/// <remarks>
/// Lives in <c>Pigmentor.Core</c> (not <c>Pigmentor.Routing</c>) so that the
/// <see cref="Pigmentor.Core.Abstractions.IVectorStore"/> port and every
/// adapter can take a pre-filter without a Core → Routing dependency. The
/// LLM-driven <c>QueryConstructor</c> that <em>produces</em> filters stays in
/// Routing; only the value type that travels into the store lives here.
/// </remarks>
public sealed class MetadataFilter
{
    /// <summary>The underlying predicate expression, the source for compilation to a store's native filter language.</summary>
    public Expression<Func<DocumentMetadata, bool>> Predicate { get; }
    private readonly Func<DocumentMetadata, bool> _compiled;

    private MetadataFilter(Expression<Func<DocumentMetadata, bool>> predicate)
    {
        Predicate = predicate;
        _compiled = predicate.Compile();
    }

    /// <summary>Build a filter from a C# predicate expression.</summary>
    public static MetadataFilter Where(Expression<Func<DocumentMetadata, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return new MetadataFilter(predicate);
    }

    /// <summary>The pass-everything filter.</summary>
    public static MetadataFilter All { get; } = new(_ => true);

    /// <summary>Apply the filter in process — used by the in-memory store and the eval harness.</summary>
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
        return new MetadataFilter(Expression.Lambda<Func<DocumentMetadata, bool>>(combined, p));
    }

    /// <summary>Logical OR of two filters.</summary>
    public MetadataFilter Or(MetadataFilter other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var p = Expression.Parameter(typeof(DocumentMetadata));
        var combined = Expression.OrElse(
            Expression.Invoke(Predicate, p),
            Expression.Invoke(other.Predicate, p));
        return new MetadataFilter(Expression.Lambda<Func<DocumentMetadata, bool>>(combined, p));
    }
}
