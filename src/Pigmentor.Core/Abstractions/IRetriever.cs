using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;

namespace Pigmentor.Core.Abstractions;

/// <summary>
/// Translates a free-text query into a ranked list of <see cref="RetrievalResult"/>s.
/// Multiple strategies (dense, sparse, hybrid, graph, vectorless, HyDE,
/// CRAG, RAG-Fusion, …) implement this interface; the most expressive
/// way to combine them is <em>composition</em>, not subclassing — see
/// Chapters 8, 14, 15.
/// </summary>
public interface IRetriever
{
    /// <summary>A short, stable identifier for this strategy (e.g. <c>dense</c>, <c>hybrid</c>, <c>graph</c>).</summary>
    string Strategy { get; }

    /// <summary>Retrieve up to <paramref name="topK"/> results for <paramref name="query"/>.</summary>
    Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieve up to results for <paramref name="request"/>, applying any specified
    /// metadata pre-filtering before top-K selection where supported.
    /// Default implementation forwards to <see cref="RetrieveAsync(string, int, CancellationToken)"/>
    /// if no filter is requested, or throws <see cref="NotSupportedException"/> if a filter is provided
    /// but the retriever does not support it.
    /// </summary>
    Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Filter is not null)
        {
            throw new NotSupportedException($"Retriever '{Strategy}' does not support metadata filtering.");
        }

        return RetrieveAsync(request.Query, request.TopK, cancellationToken);
    }

    /// <summary>
    /// Retrieve up to <paramref name="topK"/> results for <paramref name="query"/> with an optional metadata filter.
    /// </summary>
    Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        string query,
        int topK,
        MetadataFilter? filter,
        CancellationToken cancellationToken = default) =>
        RetrieveAsync(new RetrievalRequest(query, topK, filter), cancellationToken);
}
