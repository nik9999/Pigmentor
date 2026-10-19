using Pigmentor.Core.Documents;

namespace Pigmentor.Core.Abstractions;

/// <summary>
/// Splits a <see cref="Document"/> into a stream of <see cref="DocumentChunk"/>s.
/// Concrete strategies (fixed-size, sentence, recursive, semantic, code-aware,
/// contextual-retrieval) are introduced in Chapter 4.
/// </summary>
public interface IChunker
{
    /// <summary>
    /// A short, stable identifier for this strategy (e.g. <c>fixed-size</c>,
    /// <c>recursive</c>, <c>contextual</c>). Surfaces in OpenTelemetry
    /// attributes and chunk-strategy A/B tests.
    /// </summary>
    string Strategy { get; }

    /// <summary>Yield chunks from <paramref name="document"/>.</summary>
    IAsyncEnumerable<DocumentChunk> ChunkAsync(Document document, CancellationToken cancellationToken = default);
}
