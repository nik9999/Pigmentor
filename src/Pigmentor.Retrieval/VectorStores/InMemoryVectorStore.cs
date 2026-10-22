using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;
using Pigmentor.Core.Numerics;

namespace Pigmentor.Retrieval.VectorStores;

/// <summary>
/// Trivial in-memory <see cref="IVectorStore"/> backed by a <c>List</c> with
/// brute-force cosine similarity. Use for unit tests, the Hello-World
/// sample, and the development inner loop where a real vector DB would
/// be overkill.
/// </summary>
public sealed class InMemoryVectorStore : IVectorStore
{
    private readonly List<EmbeddedChunk> _chunks = [];
    private readonly Lock _lock = new();

    public InMemoryVectorStore(string collectionName = "in-memory")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        CollectionName = collectionName;
    }

    public string CollectionName { get; }

    public Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        // Always exists. Provided for symmetry with the production implementations.
        return Task.CompletedTask;
    }

    public Task UpsertAsync(IEnumerable<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        lock (_lock)
        {
            foreach (var c in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var existing = _chunks.FindIndex(e => e.Chunk.ChunkId == c.Chunk.ChunkId);
                if (existing >= 0)
                {
                    _chunks[existing] = c;
                }
                else
                {
                    _chunks.Add(c);
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        ReadOnlyMemory<float> queryVector,
        int topK,
        MetadataFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        EmbeddedChunk[] snapshot;
        lock (_lock) { snapshot = [.. _chunks]; }

        // True pre-filter: restrict the candidate set to chunks whose metadata
        // satisfies the filter BEFORE ranking, so the top-K is drawn from the
        // matching subset (not a post-filter that could leave fewer than K).
        var ranked = snapshot
            .Where(c => filter is null || filter.Matches(c.Chunk.Metadata))
            .Select(c =>
            {
                var cos = CosineKernel.Cosine(queryVector.Span, c.Vector.Span);
                return new RetrievalResult(c.Chunk, cos) { DenseScore = cos };
            })
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();

        return Task.FromResult<IReadOnlyList<RetrievalResult>>(ranked);
    }

    public Task DeleteAsync(IEnumerable<string> chunkIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunkIds);
        var ids = chunkIds.ToHashSet(StringComparer.Ordinal);
        lock (_lock)
        {
            _chunks.RemoveAll(c => ids.Contains(c.Chunk.ChunkId));
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DocumentChunk>> GetAllChunksAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<DocumentChunk>>(_chunks.Select(c => c.Chunk).ToList());
        }
    }
}
