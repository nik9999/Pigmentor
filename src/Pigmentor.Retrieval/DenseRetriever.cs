using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval;

/// <summary>
/// Dense retriever — embeds the query with the same model used at index
/// time and asks the underlying <see cref="IVectorStore"/> for the nearest
/// neighbors. Routing through <see cref="IEmbeddingService.EmbedQueryAsync"/>
/// (rather than a raw <c>IEmbeddingGenerator</c>) guarantees the query is
/// embedded with the model's <em>query</em> task prefix — the matching half of
/// the document prefix applied at index time.
/// </summary>
public sealed class DenseRetriever : IRetriever
{
    private readonly IEmbeddingService _embeddings;
    private readonly IVectorStore _store;

    public DenseRetriever(IEmbeddingService embeddings, IVectorStore store)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(store);
        _embeddings = embeddings;
        _store = store;
    }

    public string Strategy => "dense";

    public async Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);

        var queryVec = await _embeddings.EmbedQueryAsync(query, cancellationToken).ConfigureAwait(false);
        return await _store.SearchAsync(queryVec, topK, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
