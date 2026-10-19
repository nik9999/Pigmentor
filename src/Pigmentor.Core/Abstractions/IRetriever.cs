using Pigmentor.Core.Documents;

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
}
