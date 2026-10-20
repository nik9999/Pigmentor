using Microsoft.Extensions.AI;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.App.Runtime;

/// <summary>
/// Bridges <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> to the domain <see cref="IEmbeddingService"/>.
/// Handles chunk embedding, query embedding, and streaming chunk batches.
/// </summary>
public sealed class GeneratorEmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;

    public string EmbeddingModel { get; }
    public int Dimensions { get; }

    public GeneratorEmbeddingService(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        string embeddingModel,
        int dimensions)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModel);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);

        EmbeddingModel = embeddingModel;
        Dimensions = dimensions;
    }

    public async Task<EmbeddedChunk> EmbedAsync(DocumentChunk chunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        var embeddings = await _generator.GenerateAsync([chunk.Text], null, cancellationToken).ConfigureAwait(false);
        if (embeddings.Count == 0)
        {
            throw new InvalidOperationException("Embedding provider returned empty embeddings for chunk.");
        }

        return new EmbeddedChunk(chunk, embeddings[0].Vector, EmbeddingModel);
    }

    public async Task<ReadOnlyMemory<float>> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var embeddings = await _generator.GenerateAsync([query], null, cancellationToken).ConfigureAwait(false);
        if (embeddings.Count == 0)
        {
            throw new InvalidOperationException("Embedding provider returned empty embeddings for query.");
        }

        return embeddings[0].Vector;
    }

    public async IAsyncEnumerable<EmbeddedChunk> EmbedAsync(
        IAsyncEnumerable<DocumentChunk> chunks,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        // Process chunks in bounded batches of 16
        const int batchSize = 16;
        var batch = new List<DocumentChunk>(batchSize);

        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            batch.Add(chunk);
            if (batch.Count >= batchSize)
            {
                var embeddings = await ProcessBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                foreach (var item in embeddings)
                {
                    yield return item;
                }
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            var embeddings = await ProcessBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            foreach (var item in embeddings)
            {
                yield return item;
            }
            batch.Clear();
        }
    }

    private async Task<List<EmbeddedChunk>> ProcessBatchAsync(
        List<DocumentChunk> batch,
        CancellationToken cancellationToken)
    {
        var texts = batch.Select(c => c.Text);
        var response = await _generator.GenerateAsync(texts, null, cancellationToken).ConfigureAwait(false);

        if (response.Count != batch.Count)
        {
            throw new InvalidOperationException(
                $"Embedding provider returned {response.Count} embeddings for batch of {batch.Count} chunks.");
        }

        var results = new List<EmbeddedChunk>(batch.Count);
        for (var i = 0; i < batch.Count; i++)
        {
            results.Add(new EmbeddedChunk(batch[i], response[i].Vector, EmbeddingModel));
        }

        return results;
    }
}

