using System.Globalization;
using System.Text;
using Azure;
using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;
using Pigmentor.Retrieval.Filtering;

namespace Pigmentor.Retrieval.VectorStores;

/// <summary>
/// Azure AI Search-backed <see cref="IVectorStore"/>. Uses the official
/// <c>Azure.Search.Documents</c> SDK: a <see cref="SearchIndexClient"/> to
/// create/update the index and a <see cref="SearchClient"/> for upsert,
/// vector search, and delete.
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="EmbeddedChunk"/> becomes one search document. The document
/// key is a reversible (base64url) encoding of the chunk's
/// <see cref="DocumentChunk.ChunkId"/> — Azure document keys only allow
/// <c>[A-Za-z0-9_-=]</c>, so the raw <c>{DocumentId}#{ChunkIndex}</c> id cannot
/// be used directly. The chunk text and full <see cref="DocumentMetadata"/>
/// ride in retrievable fields so a search round-trip reconstructs a complete
/// <see cref="DocumentChunk"/> without a separate document store — the same
/// payload-co-location decision the Qdrant adapter makes.
/// </para>
/// <para>
/// The index is created with a cosine HNSW vector profile over a
/// <c>Collection(Edm.Single)</c> field. Search runs a vectorized k-NN query via
/// <see cref="VectorizedQuery"/>; no query text is sent (embedding happens
/// before the store, per <see cref="IVectorStore"/>).
/// </para>
/// </remarks>
public sealed class AzureAiSearchVectorStore : IVectorStore
{
    internal const string VectorProfileName = "smartdocs-vector-profile";
    internal const string HnswConfigName = "smartdocs-hnsw";
    internal const string VectorFieldName = "vector";
    internal const string KeyFieldName = "id";

    private readonly SearchIndexClient _indexClient;
    private readonly SearchClient _searchClient;
    private readonly int _vectorSize;

    /// <param name="endpoint">The Azure AI Search service endpoint, e.g. <c>https://my-svc.search.windows.net</c>.</param>
    /// <param name="indexName">The index backing this store (its <see cref="CollectionName"/>).</param>
    /// <param name="vectorSize">Embedding dimensionality. Must match the model used at index time.</param>
    /// <param name="credential">
    /// An <c>AzureKeyCredential</c> (admin key). No credentials are hardcoded —
    /// pass them from configuration. Use the <see cref="TokenCredential"/>
    /// overload for Entra ID / managed identity.
    /// </param>
    public AzureAiSearchVectorStore(
        Uri endpoint,
        string indexName,
        int vectorSize,
        AzureKeyCredential credential)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vectorSize);
        ArgumentNullException.ThrowIfNull(credential);

        CollectionName = indexName;
        _vectorSize = vectorSize;
        _indexClient = new SearchIndexClient(endpoint, credential);
        _searchClient = new SearchClient(endpoint, indexName, credential);
    }

    /// <param name="endpoint">The Azure AI Search service endpoint.</param>
    /// <param name="indexName">The index backing this store.</param>
    /// <param name="vectorSize">Embedding dimensionality.</param>
    /// <param name="credential">An Entra ID <see cref="TokenCredential"/> (e.g. <c>DefaultAzureCredential</c>).</param>
    public AzureAiSearchVectorStore(
        Uri endpoint,
        string indexName,
        int vectorSize,
        TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vectorSize);
        ArgumentNullException.ThrowIfNull(credential);

        CollectionName = indexName;
        _vectorSize = vectorSize;
        _indexClient = new SearchIndexClient(endpoint, credential);
        _searchClient = new SearchClient(endpoint, indexName, credential);
    }

    public string CollectionName { get; }

    public async Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        var index = BuildIndex(CollectionName, _vectorSize);
        // CreateOrUpdate is idempotent: creates on first call, no-ops when the
        // schema already matches. Mirrors Qdrant's EnsureCollectionExistsAsync.
        await _indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task UpsertAsync(IEnumerable<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var docs = chunks.Select(ToSearchDocument).ToList();
        if (docs.Count == 0)
        {
            return;
        }
        await _searchClient.MergeOrUploadDocumentsAsync(docs, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        int topK,
        MetadataFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);

        // Prefer a true pre-filter: translate the MetadataFilter into an OData
        // $filter that Azure AI Search evaluates server-side over the filterable
        // fields. If the expression uses a node the OData translator does not
        // cover, fall back to in-process post-filtering on filter.Matches.
        var odata = filter is null ? null : AzureSearchFilterCompiler.TryCompile(filter);
        var usePostFilter = filter is not null && odata is null;

        var options = new SearchOptions
        {
            // When post-filtering we over-fetch so the in-process pass can still
            // yield up to topK after dropping non-matching hits.
            Size = usePostFilter ? topK * 4 : topK,
            Filter = odata,
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(queryVector)
                    {
                        KNearestNeighborsCount = usePostFilter ? topK * 4 : topK,
                        Fields = { VectorFieldName },
                    },
                },
            },
        };

        // searchText null => pure vector k-NN (no BM25 leg). Hybrid is Ch 14.
        var response = await _searchClient
            .SearchAsync<SearchDocumentRecord>(searchText: null, options, cancellationToken)
            .ConfigureAwait(false);

        var results = new List<RetrievalResult>(topK);
        await foreach (var hit in response.Value.GetResultsAsync().ConfigureAwait(false))
        {
            var chunk = BuildChunk(hit.Document);
            // post-filter fallback: only when the OData translation was impractical.
            if (usePostFilter && !filter!.Matches(chunk.Metadata))
            {
                continue;
            }
            results.Add(new RetrievalResult(chunk, hit.Score ?? 0d));
            if (results.Count >= topK)
            {
                break;
            }
        }
        return results;
    }

    public async Task DeleteAsync(IEnumerable<string> chunkIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunkIds);
        var keys = chunkIds.Select(EncodeKey).ToList();
        if (keys.Count == 0)
        {
            return;
        }
        await _searchClient
            .DeleteDocumentsAsync(KeyFieldName, keys, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the index schema: a key field, the cosine HNSW vector field, and
    /// retrievable chunk-text + metadata fields. Extracted so the schema is
    /// assertable in a unit test without a live service.
    /// </summary>
    internal static SearchIndex BuildIndex(string indexName, int vectorSize)
    {
        var vectorSearch = new VectorSearch();
        vectorSearch.Algorithms.Add(new HnswAlgorithmConfiguration(HnswConfigName)
        {
            Parameters = new HnswParameters { Metric = VectorSearchAlgorithmMetric.Cosine },
        });
        vectorSearch.Profiles.Add(new VectorSearchProfile(VectorProfileName, HnswConfigName));

        var fields = new List<SearchField>
        {
            new SimpleField(KeyFieldName, SearchFieldDataType.String) { IsKey = true },
            new VectorSearchField(VectorFieldName, vectorSize, VectorProfileName),
            new SimpleField("chunk_id", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("document_id", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("chunk_index", SearchFieldDataType.Int32),
            new SearchableField("text"),
            new SimpleField("start_offset", SearchFieldDataType.Int32),
            new SimpleField("end_offset", SearchFieldDataType.Int32),
            new SimpleField("silo", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("department", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("office", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("confidentiality", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("document_type", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("fiscal_year", SearchFieldDataType.Int32) { IsFilterable = true },
            new SimpleField("author", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("last_modified", SearchFieldDataType.String),
            new SearchableField("title"),
        };

        return new SearchIndex(indexName, fields) { VectorSearch = vectorSearch };
    }

    internal static SearchDocumentRecord ToSearchDocument(EmbeddedChunk e)
    {
        var m = e.Chunk.Metadata;
        return new SearchDocumentRecord
        {
            Id = EncodeKey(e.Chunk.ChunkId),
            Vector = e.Vector.ToArray(),
            ChunkId = e.Chunk.ChunkId,
            DocumentId = e.Chunk.DocumentId,
            ChunkIndex = e.Chunk.ChunkIndex,
            Text = e.Chunk.Text,
            StartOffset = e.Chunk.StartCharOffset,
            EndOffset = e.Chunk.EndCharOffset,
            Silo = m.Silo,
            Department = m.Department,
            Office = m.Office,
            Confidentiality = m.ConfidentialityLevel,
            DocumentType = m.DocumentType,
            FiscalYear = m.FiscalYear,
            Author = m.Author,
            LastModified = m.LastModified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Title = m.Title,
        };
    }

    internal static DocumentChunk BuildChunk(SearchDocumentRecord d)
    {
        ArgumentNullException.ThrowIfNull(d);
        var meta = new DocumentMetadata(
            Id: d.DocumentId,
            Silo: d.Silo,
            Department: d.Department,
            Office: d.Office,
            ConfidentialityLevel: d.Confidentiality,
            DocumentType: d.DocumentType,
            FiscalYear: d.FiscalYear,
            Author: d.Author,
            LastModified: DateOnly.ParseExact(d.LastModified, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            Title: d.Title);

        return new DocumentChunk(
            ChunkId: d.ChunkId,
            DocumentId: d.DocumentId,
            ChunkIndex: d.ChunkIndex,
            Text: d.Text,
            StartCharOffset: d.StartOffset,
            EndCharOffset: d.EndOffset,
            Metadata: meta);
    }

    /// <summary>
    /// Reversibly encodes a raw ChunkId (which may contain '#') into a valid
    /// Azure document key using URL-safe base64. Deterministic, so re-ingesting
    /// the same chunk targets the same document and upsert stays idempotent.
    /// </summary>
    internal static string EncodeKey(string chunkId)
    {
        ArgumentNullException.ThrowIfNull(chunkId);
        var bytes = Encoding.UTF8.GetBytes(chunkId);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
