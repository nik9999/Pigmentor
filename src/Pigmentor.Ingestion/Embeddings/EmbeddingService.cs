using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Embeddings;

/// <summary>
/// Default <see cref="IEmbeddingService"/> implementation. Wraps an
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> from
/// Microsoft.Extensions.AI and adds the Pigmentor domain concerns:
/// model-name capture on every <see cref="EmbeddedChunk"/> and a
/// streaming <see cref="EmbedAsync(IAsyncEnumerable{DocumentChunk}, CancellationToken)"/>
/// overload (the batched, rate-limited variant lives in
/// <see cref="BatchEmbeddingPipeline"/>).
/// </summary>
public sealed class EmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly EmbeddingPrompt _prompt;
    private readonly ILogger<EmbeddingService> _logger;

    /// <summary>Create the service over an <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>.</summary>
    /// <param name="generator">The underlying Microsoft.Extensions.AI generator.</param>
    /// <param name="embeddingModel">The embedding-model identifier captured on every vector.</param>
    /// <param name="dimensions">The dimensionality of the produced vectors.</param>
    /// <param name="logger">Diagnostics logger.</param>
    /// <param name="prompt">
    /// The per-model task-instruction prefix scheme. Defaults to
    /// <see cref="EmbeddingPrompt.None"/> (correct for OpenAI / Azure OpenAI, which are
    /// trained without prefixes). Supply <see cref="EmbeddingPrompt.Nomic"/> /
    /// <see cref="EmbeddingPrompt.Mxbai"/> when the model expects task prefixes.
    /// </param>
    public EmbeddingService(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        string embeddingModel,
        int dimensions,
        ILogger<EmbeddingService> logger,
        EmbeddingPrompt? prompt = null)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModel);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentNullException.ThrowIfNull(logger);

        _generator = generator;
        EmbeddingModel = embeddingModel;
        Dimensions = dimensions;
        _logger = logger;
        _prompt = prompt ?? EmbeddingPrompt.None;
    }

    /// <inheritdoc />
    public string EmbeddingModel { get; }

    /// <inheritdoc />
    public int Dimensions { get; }

    /// <inheritdoc />
    public async Task<EmbeddedChunk> EmbedAsync(DocumentChunk chunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var generated = await _generator.GenerateAsync(
            [_prompt.Apply(chunk.Text, EmbeddingTaskType.Document)],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new EmbeddedChunk(chunk, generated[0].Vector, EmbeddingModel);
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<float>> EmbedQueryAsync(
        string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var generated = await _generator.GenerateAsync(
            [_prompt.Apply(query, EmbeddingTaskType.Query)],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return generated[0].Vector;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<EmbeddedChunk> EmbedAsync(
        IAsyncEnumerable<DocumentChunk> chunks,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return await EmbedAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
    }
}
