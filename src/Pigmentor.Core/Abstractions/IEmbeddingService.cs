using Pigmentor.Core.Documents;

namespace Pigmentor.Core.Abstractions;

/// <summary>
/// Produces dense embedding vectors for <see cref="DocumentChunk"/>s.
/// This is the Pigmentor domain port — internally, implementations delegate
/// to <c>Microsoft.Extensions.AI.IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c>
/// and add domain concerns (rate limiting, model-name capture, batching).
/// Chapter 3 introduces the default implementation.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>The embedding-model identifier this service produces vectors for.</summary>
    string EmbeddingModel { get; }

    /// <summary>The dimensionality of the vectors this service produces.</summary>
    int Dimensions { get; }

    /// <summary>Embed a single chunk.</summary>
    Task<EmbeddedChunk> EmbedAsync(DocumentChunk chunk, CancellationToken cancellationToken = default);

    /// <summary>
    /// Embed a free-text query for retrieval. Distinct from
    /// <see cref="EmbedAsync(DocumentChunk, CancellationToken)"/> because retrieval
    /// models often require a query-specific instruction prefix; sending the document
    /// scheme for a query (or no scheme) degrades recall.
    /// </summary>
    Task<ReadOnlyMemory<float>> EmbedQueryAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Embed a stream of chunks. Implementations are expected to batch per
    /// the underlying provider's request-size limits and to respect
    /// rate-limit responses (<c>429 Too Many Requests</c>) using Polly.
    /// </summary>
    IAsyncEnumerable<EmbeddedChunk> EmbedAsync(
        IAsyncEnumerable<DocumentChunk> chunks,
        CancellationToken cancellationToken = default);
}
