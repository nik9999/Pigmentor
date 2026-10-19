using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;

namespace Pigmentor.Core.Abstractions;

/// <summary>
/// Pigmentor' domain port for any vector database. Different from
/// <c>Microsoft.Extensions.VectorData.VectorStore</c> (which is the abstract
/// class adapters target) by intent: this interface is shaped around
/// Pigmentor domain types (<see cref="EmbeddedChunk"/>, <see cref="RetrievalResult"/>)
/// and exposes only the operations the book actually uses.
/// The primary persistent implementation is SqliteVecStore (Pigmentor.VectorStore.SqliteVec).
/// </summary>
public interface IVectorStore
{
    /// <summary>The logical collection / index name.</summary>
    string CollectionName { get; }

    /// <summary>Idempotently create the underlying collection if it does not yet exist.</summary>
    Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default);

    /// <summary>Insert or update a batch of embedded chunks.</summary>
    Task UpsertAsync(IEnumerable<EmbeddedChunk> chunks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Search for the <paramref name="topK"/> chunks whose vectors are most
    /// similar to <paramref name="queryVector"/>. The semantic of "most similar"
    /// (cosine, dot product, Euclidean) is fixed at collection-creation time
    /// per the underlying store's configuration; see Ch 3 for the distance-metric
    /// trade-offs.
    /// </summary>
    /// <param name="queryVector">The query embedding to rank candidates against.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <param name="filter">
    /// Optional metadata pre-filter (Chapter 11). When non-null, the store
    /// restricts the candidate set to chunks whose <see cref="DocumentMetadata"/>
    /// satisfies the filter <em>before</em> ranking — a true pre-filter, not a
    /// post-filter — so the top-K is drawn from the matching subset.
    /// <see langword="null"/> (the default) matches all chunks.
    /// </param>
    /// <param name="cancellationToken">Cancels the search.</param>
    Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        int topK,
        MetadataFilter? filter = null,
        CancellationToken cancellationToken = default);

    /// <summary>Delete the chunks with the given <paramref name="chunkIds"/>.</summary>
    Task DeleteAsync(IEnumerable<string> chunkIds, CancellationToken cancellationToken = default);
}
