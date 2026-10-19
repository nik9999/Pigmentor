using Pigmentor.Core.Filtering;

namespace Pigmentor.Core.Abstractions;

/// <summary>
/// Encapsulates parameters for a retrieval operation against an <see cref="IRetriever"/>.
/// </summary>
public sealed record RetrievalRequest
{
    /// <summary>The search query string.</summary>
    public string Query { get; init; }

    /// <summary>The maximum number of results to retrieve.</summary>
    public int TopK { get; init; }

    /// <summary>Optional metadata filter to restrict candidates before top-K ranking.</summary>
    public MetadataFilter? Filter { get; init; }

    public RetrievalRequest(string query, int topK, MetadataFilter? filter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);

        Query = query;
        TopK = topK;
        Filter = filter;
    }
}

