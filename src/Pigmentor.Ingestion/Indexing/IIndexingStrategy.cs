using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Indexing;

/// <summary>
/// Translates a stream of <see cref="DocumentChunk"/>s into a stream of
/// "text-to-embed" pairs: each pair is a piece of text that gets embedded
/// (the "key"), bound to the chunk that should be returned at retrieval
/// time (the "payload"). Chapter 7 ships four implementations:
///
/// <list type="bullet">
///   <item><description>Chunk indexing — embed and retrieve the chunk itself.</description></item>
///   <item><description>Sub-chunk indexing — embed the first proposition, retrieve the parent chunk.</description></item>
///   <item><description>Query indexing — embed LLM-generated hypothetical questions, retrieve the source chunk.</description></item>
///   <item><description>Summary indexing — embed an LLM-generated summary, retrieve the original chunk.</description></item>
/// </list>
/// </summary>
public interface IIndexingStrategy
{
    /// <summary>Short, stable identifier for the strategy (e.g. <c>chunk</c>, <c>query</c>).</summary>
    string Strategy { get; }

    /// <summary>
    /// Project a chunk into one or more <see cref="IndexedItem"/>s. Each item carries
    /// the text that the embedding service will encode and the chunk that
    /// should be returned to the caller when that embedding is the
    /// nearest-neighbor match.
    /// </summary>
    IAsyncEnumerable<IndexedItem> IndexAsync(DocumentChunk chunk, CancellationToken cancellationToken = default);
}

/// <summary>An ((embedding-input text), (retrieval-time payload chunk)) pair.</summary>
public sealed record IndexedItem(string EmbedText, DocumentChunk Payload);
