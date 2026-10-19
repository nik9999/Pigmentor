using Azure;
using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Retrieval.VectorStores;

namespace Pigmentor.Retrieval.Hybrid;

/// <summary>
/// Single-store hybrid retriever over Azure AI Search. A single
/// <see cref="SearchClient.SearchAsync{T}(string, SearchOptions, System.Threading.CancellationToken)"/>
/// call combines the keyword leg (the <c>searchText</c>, scored with Azure's
/// BM25) and the vector leg (a <see cref="VectorizedQuery"/> over the embedding
/// field); Azure AI Search fuses the two internally with Reciprocal Rank Fusion.
/// This is the "the database is the hybrid" path Chapter 14 teaches — dense,
/// sparse, and fusion all happen in the service, not in process (contrast
/// <see cref="Pigmentor.Retrieval.HybridRetriever"/>, which fans out and fuses in .NET).
/// </summary>
/// <remarks>
/// <para>
/// The query embedding is produced by the injected <see cref="IEmbeddingService"/>
/// (mirroring the dense retriever's query-task prefix) and sent on the vector
/// field; the same raw query string drives the keyword leg.
/// </para>
/// <para>
/// The optional semantic ranker (off by default) is a SEPARATE rerank layer: it
/// re-orders the top fused candidates with a cross-encoder-style model AFTER the
/// vector+keyword RRF fusion. It is NOT the fusion step itself — enabling it adds
/// an L2 rerank on top of the hybrid L1 ranking, at additional cost and latency.
/// </para>
/// <para>
/// Integration adapter: build-verified and correct-by-construction, but not run
/// in CI (it needs a live Azure AI Search index) — the same policy as
/// <see cref="AzureAiSearchVectorStore"/>.
/// </para>
/// </remarks>
public sealed class AzureAiSearchHybridRetriever : IRetriever
{
    private readonly SearchClient _searchClient;
    private readonly IEmbeddingService _embeddings;
    private readonly string _vectorFieldName;
    private readonly string? _semanticConfigurationName;

    /// <summary>
    /// Creates the retriever using an admin <see cref="AzureKeyCredential"/>.
    /// </summary>
    /// <param name="endpoint">The Azure AI Search service endpoint.</param>
    /// <param name="indexName">The index to search.</param>
    /// <param name="credential">An <c>AzureKeyCredential</c> (query or admin key) — never hardcoded.</param>
    /// <param name="embeddings">Embeds the query (query-task prefix applied).</param>
    /// <param name="vectorFieldName">The index's vector field. Defaults to the store's <c>vector</c> field.</param>
    /// <param name="semanticConfigurationName">
    /// Optional semantic-configuration name. When set, the semantic ranker reranks
    /// the fused results (a separate L2 layer). <see langword="null"/> (default)
    /// leaves the ranker off.
    /// </param>
    public AzureAiSearchHybridRetriever(
        Uri endpoint,
        string indexName,
        AzureKeyCredential credential,
        IEmbeddingService embeddings,
        string vectorFieldName = AzureAiSearchVectorStore.VectorFieldName,
        string? semanticConfigurationName = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentException.ThrowIfNullOrWhiteSpace(vectorFieldName);

        _searchClient = new SearchClient(endpoint, indexName, credential);
        _embeddings = embeddings;
        _vectorFieldName = vectorFieldName;
        _semanticConfigurationName = semanticConfigurationName;
    }

    /// <summary>
    /// Creates the retriever using an Entra ID <see cref="TokenCredential"/>
    /// (e.g. <c>DefaultAzureCredential</c> / managed identity).
    /// </summary>
    /// <param name="endpoint">The Azure AI Search service endpoint.</param>
    /// <param name="indexName">The index to search.</param>
    /// <param name="credential">An Entra ID <see cref="TokenCredential"/>.</param>
    /// <param name="embeddings">Embeds the query (query-task prefix applied).</param>
    /// <param name="vectorFieldName">The index's vector field. Defaults to the store's <c>vector</c> field.</param>
    /// <param name="semanticConfigurationName">
    /// Optional semantic-configuration name. When set, the semantic ranker reranks
    /// the fused results (a separate L2 layer). <see langword="null"/> (default)
    /// leaves the ranker off.
    /// </param>
    public AzureAiSearchHybridRetriever(
        Uri endpoint,
        string indexName,
        TokenCredential credential,
        IEmbeddingService embeddings,
        string vectorFieldName = AzureAiSearchVectorStore.VectorFieldName,
        string? semanticConfigurationName = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentException.ThrowIfNullOrWhiteSpace(vectorFieldName);

        _searchClient = new SearchClient(endpoint, indexName, credential);
        _embeddings = embeddings;
        _vectorFieldName = vectorFieldName;
        _semanticConfigurationName = semanticConfigurationName;
    }

    public string Strategy => "hybrid-azure";

    public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default) =>
        RetrieveAsync(new RetrievalRequest(query, topK), cancellationToken);

    public async Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Filter is not null)
        {
            throw new NotSupportedException($"Retriever '{Strategy}' does not support metadata filtering.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.TopK);

        var queryVec = await _embeddings.EmbedQueryAsync(request.Query, cancellationToken).ConfigureAwait(false);

        var options = new SearchOptions
        {
            Size = request.TopK,
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(queryVec)
                    {
                        KNearestNeighborsCount = request.TopK,
                        Fields = { _vectorFieldName },
                    },
                },
            },
        };

        // Optional L2 semantic rerank — a separate layer on top of the hybrid
        // RRF fusion, not the fusion itself. Off unless a config name was given.
        if (!string.IsNullOrWhiteSpace(_semanticConfigurationName))
        {
            options.QueryType = SearchQueryType.Semantic;
            options.SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = _semanticConfigurationName,
            };
        }

        // searchText is the keyword (BM25) leg; the VectorizedQuery is the dense
        // leg. Azure AI Search fuses the two server-side with RRF.
        var response = await _searchClient
            .SearchAsync<SearchDocumentRecord>(searchText: request.Query, options, cancellationToken)
            .ConfigureAwait(false);

        var results = new List<RetrievalResult>(request.TopK);
        await foreach (var hit in response.Value.GetResultsAsync().ConfigureAwait(false))
        {
            results.Add(new RetrievalResult(AzureAiSearchVectorStore.BuildChunk(hit.Document), hit.Score ?? 0d));
            if (results.Count >= request.TopK)
            {
                break;
            }
        }
        return results;
    }
}
