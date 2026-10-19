using System.Globalization;
using System.Text.RegularExpressions;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval.Hybrid;

/// <summary>
/// Single-store hybrid retriever over a Qdrant collection that holds BOTH a
/// dense (named) vector and a sparse (named) vector per point. It issues ONE
/// <c>QueryAsync</c> built from two <see cref="PrefetchQuery"/> legs — a dense
/// nearest-neighbor leg and a sparse leg — and lets Qdrant fuse them
/// <em>server-side</em> with <see cref="Fusion.Rrf"/> (the Query API, Qdrant
/// 1.10+). The collection's sparse vector should be configured with
/// <see cref="Modifier.Idf"/> so Qdrant applies IDF weighting to the sparse
/// leg at query time.
/// </summary>
/// <remarks>
/// <para>
/// This is the "the database is the hybrid" counterpart to the in-process
/// <see cref="Pigmentor.Retrieval.HybridRetriever"/>: dense + sparse + RRF all happen in
/// the engine, not in .NET. The dense vector is produced by the injected
/// <see cref="IEmbeddingService"/>; the sparse query vector is built from the
/// query terms (see <see cref="BuildSparseQueryVector"/>).
/// </para>
/// <para>
/// Integration adapter: build-verified and correct-by-construction, but not
/// run in CI (it needs a live Qdrant with a hybrid collection) — the same
/// policy as <see cref="VectorStores.QdrantVectorStore"/>.
/// </para>
/// </remarks>
public sealed partial class QdrantHybridRetriever : IRetriever
{
    /// <summary>The RRF prefetch fan-out multiplier — each leg fetches <c>topK × this</c> candidates before fusion.</summary>
    private const int CandidateMultiplier = 4;

    /// <summary>
    /// Size of the sparse-index hashing space. Query terms are hashed into
    /// <c>[0, SparseIndexSpace)</c>. With IDF enabled on the collection, the
    /// per-term weights below are scaled by Qdrant's IDF at query time.
    /// </summary>
    private const uint SparseIndexSpace = 1_000_000u;

    private readonly QdrantClient _client;
    private readonly IEmbeddingService _embeddings;
    private readonly string _collectionName;
    private readonly string _denseVectorName;
    private readonly string _sparseVectorName;

    /// <param name="client">An initialized Qdrant gRPC client (default port 6334).</param>
    /// <param name="embeddings">Embeds the query (query-task prefix applied), mirroring the dense retriever.</param>
    /// <param name="collectionName">The hybrid Qdrant collection (named dense + sparse vectors).</param>
    /// <param name="denseVectorName">The collection's named dense vector. Default <c>dense</c>.</param>
    /// <param name="sparseVectorName">The collection's named sparse vector (IDF-modified). Default <c>sparse</c>.</param>
    public QdrantHybridRetriever(
        QdrantClient client,
        IEmbeddingService embeddings,
        string collectionName,
        string denseVectorName = "dense",
        string sparseVectorName = "sparse")
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(denseVectorName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sparseVectorName);

        _client = client;
        _embeddings = embeddings;
        _collectionName = collectionName;
        _denseVectorName = denseVectorName;
        _sparseVectorName = sparseVectorName;
    }

    public string Strategy => "hybrid-qdrant";

    [GeneratedRegex(@"[\p{L}\d_]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    public async Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);

        var denseVec = await _embeddings.EmbedQueryAsync(query, cancellationToken).ConfigureAwait(false);
        var (sparseValues, sparseIndices) = BuildSparseQueryVector(query);

        var candidateLimit = (ulong)(topK * CandidateMultiplier);

        // Dense prefetch leg: nearest neighbors over the named dense vector.
        var densePrefetch = new PrefetchQuery
        {
            Query = denseVec.ToArray(), // float[] -> VectorInput -> Query (dense nearest)
            Using = _denseVectorName,
            Limit = candidateLimit,
        };

        // Sparse prefetch leg: (values, indices) tuple -> sparse Query. With
        // Modifier.Idf configured on the collection's sparse vector, Qdrant
        // applies IDF weighting to these term weights at query time.
        var sparsePrefetch = new PrefetchQuery
        {
            Query = (sparseValues, sparseIndices),
            Using = _sparseVectorName,
            Limit = candidateLimit,
        };

        // Server-side Reciprocal Rank Fusion over the two prefetch legs.
        var scored = await _client.QueryAsync(
            collectionName: _collectionName,
            query: Fusion.Rrf, // Fusion -> Query (fuse the prefetch results)
            prefetch: [densePrefetch, sparsePrefetch],
            limit: (ulong)topK,
            payloadSelector: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return [.. scored.Select(p => new RetrievalResult(BuildChunk(p.Payload), p.Score))];
    }

    /// <summary>
    /// Builds a sparse query vector (parallel <c>values</c> / <c>indices</c>
    /// arrays, the form Qdrant's <see cref="Query"/> accepts) from the query
    /// terms. Tokenization matches <see cref="Pigmentor.Retrieval.SparseRetriever"/> — the
    /// same Unicode word regex, lower-cased — so the query lexicon lines up with
    /// the corpus side. Each distinct term contributes a raw term-frequency
    /// weight at a deterministic hashed index; Qdrant's <see cref="Modifier.Idf"/>
    /// (configured on the collection) supplies the IDF factor server-side, so
    /// this method intentionally does NOT pre-weight by IDF.
    /// </summary>
    /// <remarks>
    /// Hashing terms into a fixed index space is the standard "term → sparse
    /// dimension" trick: it avoids shipping a vocabulary map to the client. The
    /// hash is FNV-1a over the UTF-16 bytes, reduced modulo
    /// <see cref="SparseIndexSpace"/>. Indices are de-duplicated so repeated
    /// terms accumulate their term frequency into one weight.
    /// </remarks>
    internal static (float[] Values, uint[] Indices) BuildSparseQueryVector(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var weights = new Dictionary<uint, float>();
        foreach (System.Text.RegularExpressions.Match m in TokenRegex().Matches(query))
        {
            var term = m.Value.ToLowerInvariant();
            var index = HashTerm(term);
            weights[index] = weights.TryGetValue(index, out var w) ? w + 1f : 1f;
        }

        var indices = new uint[weights.Count];
        var values = new float[weights.Count];
        var i = 0;
        foreach (var (index, weight) in weights)
        {
            indices[i] = index;
            values[i] = weight;
            i++;
        }
        return (values, indices);
    }

    /// <summary>FNV-1a hash of a term, reduced into the sparse index space.</summary>
    private static uint HashTerm(string term)
    {
        const uint OffsetBasis = 2166136261u;
        const uint Prime = 16777619u;
        var hash = OffsetBasis;
        foreach (var ch in term)
        {
            hash = (hash ^ ch) * Prime;
        }
        return hash % SparseIndexSpace;
    }

    /// <summary>
    /// Reconstructs a <see cref="DocumentChunk"/> from a Qdrant point payload.
    /// The payload schema matches what <see cref="VectorStores.QdrantVectorStore"/>
    /// writes (chunk text + full metadata co-located on the point).
    /// </summary>
    private static DocumentChunk BuildChunk(Google.Protobuf.Collections.MapField<string, Value> payload)
    {
        var meta = new DocumentMetadata(
            Id: payload["document_id"].StringValue,
            Silo: payload["silo"].StringValue,
            Department: payload["department"].StringValue,
            Office: payload["office"].StringValue,
            ConfidentialityLevel: payload["confidentiality"].StringValue,
            DocumentType: payload["document_type"].StringValue,
            FiscalYear: (int)payload["fiscal_year"].IntegerValue,
            Author: payload["author"].StringValue,
            LastModified: DateOnly.ParseExact(
                payload["last_modified"].StringValue, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            Title: payload["title"].StringValue);

        return new DocumentChunk(
            ChunkId: payload["chunk_id"].StringValue,
            DocumentId: meta.Id,
            ChunkIndex: (int)payload["chunk_index"].IntegerValue,
            Text: payload["text"].StringValue,
            StartCharOffset: (int)payload["start_offset"].IntegerValue,
            EndCharOffset: (int)payload["end_offset"].IntegerValue,
            Metadata: meta);
    }
}
