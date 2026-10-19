using System.Globalization;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;
using Pigmentor.Retrieval.Filtering;

namespace Pigmentor.Retrieval.VectorStores;

/// <summary>
/// Qdrant-backed <see cref="IVectorStore"/>. Uses the official
/// <c>Qdrant.Client</c> gRPC client.
/// </summary>
/// <remarks>
/// Each <see cref="EmbeddedChunk"/> becomes a Qdrant point keyed by a
/// stable Guid derived from the chunk's <see cref="DocumentChunk.ChunkId"/>.
/// The chunk text + full <see cref="DocumentMetadata"/> ride in the point's
/// payload so a search round-trip can reconstruct a complete
/// <see cref="DocumentChunk"/> without a separate document store.
/// </remarks>
public sealed class QdrantVectorStore : IVectorStore
{
    private readonly QdrantClient _client;
    private readonly int _vectorSize;
    private readonly Distance _distance;
    private readonly bool _useScalarQuantization;

    /// <param name="client">An initialized Qdrant gRPC client (default port 6334).</param>
    /// <param name="collectionName">The Qdrant collection backing this store.</param>
    /// <param name="vectorSize">Embedding dimensionality. Must match the model used at index time.</param>
    /// <param name="distance">Similarity metric, fixed at collection-creation time.</param>
    /// <param name="useScalarQuantization">
    /// When <see langword="true"/>, the collection is created with int8 scalar
    /// quantization (<c>quantization_config</c>) — roughly 4x smaller vectors at
    /// ~99% recall, pinned in RAM for fast rescoring. Off by default so the
    /// baseline collection layout is byte-for-byte unchanged; opt in per the
    /// recall/memory trade-off discussed in Chapter 6.
    /// </param>
    public QdrantVectorStore(
        QdrantClient client,
        string collectionName,
        int vectorSize,
        Distance distance = Distance.Cosine,
        bool useScalarQuantization = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vectorSize);

        _client = client;
        CollectionName = collectionName;
        _vectorSize = vectorSize;
        _distance = distance;
        _useScalarQuantization = useScalarQuantization;
    }

    public string CollectionName { get; }

    public async Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        if (await _client.CollectionExistsAsync(CollectionName, cancellationToken).ConfigureAwait(false))
        {
            return;
        }
        try
        {
            await _client.CreateCollectionAsync(
                CollectionName,
                BuildVectorParams(_vectorSize, _distance, _useScalarQuantization),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Idempotent creation: in a scaled-out deployment another instance can
            // create the collection between the existence check above and this
            // call (a TOCTOU race). Re-verify — swallow only if it now exists;
            // otherwise the failure was real and must propagate.
            if (!await _client.CollectionExistsAsync(CollectionName, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Builds the <see cref="VectorParams"/> for collection creation. Extracted
    /// so the quantization wiring is unit-testable without a live Qdrant.
    /// When <paramref name="useScalarQuantization"/> is on, an int8
    /// <c>ScalarQuantization</c> config is attached with its storage tier set to
    /// <see cref="Memory.Pinned"/> (Qdrant 1.19's replacement for the retired
    /// <c>always_ram</c> flag: <c>Cold</c> = disk, <c>Cached</c> = disk with a
    /// RAM cache, <c>Pinned</c> = always in RAM); otherwise the params are the
    /// bare <c>Size</c>/<c>Distance</c> baseline.
    /// </summary>
    internal static VectorParams BuildVectorParams(int vectorSize, Distance distance, bool useScalarQuantization)
    {
        var vectorParams = new VectorParams { Size = (ulong)vectorSize, Distance = distance };
        if (useScalarQuantization)
        {
            vectorParams.QuantizationConfig = new QuantizationConfig
            {
                Scalar = new ScalarQuantization
                {
                    Type = QuantizationType.Int8,
                    // Pin quantized vectors in RAM so rescoring stays fast — the
                    // memory-fit win that makes quantization worthwhile (Ch 6 §RAM).
                    // Qdrant.Client 1.19 retired the AlwaysRam bool for this tier enum.
                    Memory = Memory.Pinned,
                },
            };
        }
        return vectorParams;
    }

    public async Task UpsertAsync(IEnumerable<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var points = chunks.Select(BuildPoint).ToList();
        if (points.Count == 0)
        {
            return;
        }
        await _client.UpsertAsync(CollectionName, points, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        int topK,
        MetadataFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);

        // True pre-filter: when a MetadataFilter is supplied, compile it to a
        // native Qdrant gRPC Filter so the candidate set is narrowed inside the
        // engine before scoring. null => no filter (match all). Correct by
        // construction via QdrantFilterCompiler; exercised against a live Qdrant
        // in the integration suite (see QdrantVectorStoreTests), not in CI unit
        // tests, since it needs a running container.
        var grpcFilter = filter is null ? null : QdrantFilterCompiler.ToGrpcFilter(filter);

        // Universal Query API (Qdrant 1.10+; the only non-obsolete search surface in
        // Qdrant.Client 1.19). A float[] converts implicitly to a nearest-vector Query;
        // the hybrid retriever uses the same call with prefetch legs + RRF fusion.
        var hits = await _client.QueryAsync(
            CollectionName,
            query: queryVector.ToArray(),
            filter: grpcFilter,
            limit: (ulong)topK,
            payloadSelector: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return [.. hits.Select(h => new RetrievalResult(BuildChunk(h.Payload), h.Score))];
    }

    public async Task DeleteAsync(IEnumerable<string> chunkIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunkIds);
        var ids = chunkIds.Select(ToPointId).ToList();
        if (ids.Count == 0)
        {
            return;
        }
        await _client.DeleteAsync(CollectionName, ids, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static PointStruct BuildPoint(EmbeddedChunk e)
    {
        var p = new PointStruct
        {
            Id = ToPointId(e.Chunk.ChunkId),
            Vectors = e.Vector.ToArray(),
        };
        var m = e.Chunk.Metadata;
        p.Payload["chunk_id"] = e.Chunk.ChunkId;
        p.Payload["document_id"] = e.Chunk.DocumentId;
        p.Payload["chunk_index"] = e.Chunk.ChunkIndex;
        p.Payload["text"] = e.Chunk.Text;
        p.Payload["start_offset"] = e.Chunk.StartCharOffset;
        p.Payload["end_offset"] = e.Chunk.EndCharOffset;
        p.Payload["silo"] = m.Silo;
        p.Payload["department"] = m.Department;
        p.Payload["office"] = m.Office;
        p.Payload["confidentiality"] = m.ConfidentialityLevel;
        p.Payload["document_type"] = m.DocumentType;
        p.Payload["fiscal_year"] = m.FiscalYear;
        p.Payload["author"] = m.Author;
        p.Payload["last_modified"] = m.LastModified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        p.Payload["title"] = m.Title;
        return p;
    }

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

    /// <summary>Stable Guid v5-style derivation from a string ChunkId.</summary>
    private static PointId ToPointId(string chunkId)
    {
        // Qdrant accepts UUID or u64. We hash the chunkId into a deterministic Guid.
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(chunkId));
        var guid = new Guid(bytes.AsSpan(0, 16));
        return new PointId { Uuid = guid.ToString() };
    }
}
