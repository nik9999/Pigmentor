using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Embeddings;

/// <summary>
/// Configurable options for <see cref="BatchEmbeddingPipeline"/>.
/// </summary>
public sealed class BatchEmbeddingOptions
{
    /// <summary>How many chunks to embed per provider call. Default: 32.</summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>
    /// How many provider calls to keep in flight at once. Honored by
    /// <see cref="BatchEmbeddingPipeline"/> via a concurrency gate; output order
    /// is preserved regardless. Values below 1 are treated as 1. Default: 4.
    /// </summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>How many retry attempts on a 429 / transient failure. Default: 3.</summary>
    public int MaxRetries { get; set; } = 3;
}

/// <summary>
/// Embeds many <see cref="DocumentChunk"/>s with provider-side batching,
/// bounded concurrency, and Polly-backed exponential-backoff retry on
/// 429 / transient failures. Up to <see cref="BatchEmbeddingOptions.MaxConcurrency"/>
/// batches are kept in flight at once, yet the output order always matches the
/// input order: results are reassembled by batch index before they are yielded.
/// </summary>
public sealed partial class BatchEmbeddingPipeline
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly string _embeddingModel;
    private readonly EmbeddingPrompt _prompt;
    private readonly BatchEmbeddingOptions _options;
    private readonly ResiliencePipeline _retryPipeline;
    private readonly ILogger<BatchEmbeddingPipeline> _logger;

    public BatchEmbeddingPipeline(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        string embeddingModel,
        BatchEmbeddingOptions options,
        ILogger<BatchEmbeddingPipeline> logger,
        EmbeddingPrompt? prompt = null)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModel);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _generator = generator;
        _embeddingModel = embeddingModel;
        _options = options;
        _logger = logger;
        _prompt = prompt ?? EmbeddingPrompt.None;

        var builder = new ResiliencePipelineBuilder();
        if (options.MaxRetries >= 1)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = options.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(1),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex =>
                    ex is HttpRequestException ||
                    ex.GetType().Name.Contains("RateLimit", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("429", StringComparison.Ordinal)),
            });
        }
        _retryPipeline = builder.Build();
    }

    /// <summary>
    /// Embed a stream of chunks. Up to <see cref="BatchEmbeddingOptions.MaxConcurrency"/>
    /// provider calls run in parallel, but the yielded results are reassembled in
    /// input order — so the Nth output always corresponds to the Nth input.
    /// </summary>
    public async IAsyncEnumerable<EmbeddedChunk> EmbedAsync(
        IAsyncEnumerable<DocumentChunk> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        // Buffer the stream into fixed-size batches, preserving order. Each batch
        // carries its position so results can be reassembled deterministically.
        var batches = new List<List<DocumentChunk>>();
        var current = new List<DocumentChunk>(_options.BatchSize);
        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            current.Add(chunk);
            if (current.Count >= _options.BatchSize)
            {
                batches.Add(current);
                current = new List<DocumentChunk>(_options.BatchSize);
            }
        }
        if (current.Count > 0)
        {
            batches.Add(current);
        }

        if (batches.Count == 0)
        {
            yield break;
        }

        // Dispatch batches under a concurrency gate. Results land in an
        // index-keyed array so reassembly is order-preserving regardless of the
        // order in which the provider calls actually complete.
        var maxConcurrency = Math.Max(1, _options.MaxConcurrency);
        using var gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var results = new EmbeddedChunk[batches.Count][];
        var tasks = new Task[batches.Count];
        async Task EmbedBatchUnderGate(int idx)
        {
            try
            {
                results[idx] = await EmbedBatchAsync(batches[idx], cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        for (int i = 0; i < batches.Count; i++)
        {
            // Acquire the gate BEFORE starting the work so no more than
            // MaxConcurrency provider calls are ever in flight. EmbedBatchAsync is
            // I/O-bound (a provider HTTP call), so there is no CPU work to offload
            // onto the ThreadPool — start it directly rather than via Task.Run,
            // which would queue every batch up front only to park it on the gate.
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            tasks[i] = EmbedBatchUnderGate(i);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (var batchResult in results)
        {
            foreach (var embedded in batchResult)
            {
                yield return embedded;
            }
        }
    }

    private async Task<EmbeddedChunk[]> EmbedBatchAsync(
        List<DocumentChunk> batch,
        CancellationToken cancellationToken)
    {
        var texts = batch.Select(c => _prompt.Apply(c.Text, EmbeddingTaskType.Document)).ToArray();
        var generated = await _retryPipeline.ExecuteAsync(
            async ct => await _generator.GenerateAsync(texts, cancellationToken: ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        if (generated.Count != batch.Count)
        {
            throw new InvalidOperationException(
                $"Embedding provider returned {generated.Count} vectors for {batch.Count} inputs.");
        }

        var results = new EmbeddedChunk[batch.Count];
        for (int i = 0; i < batch.Count; i++)
        {
            results[i] = new EmbeddedChunk(batch[i], generated[i].Vector, _embeddingModel);
        }
        Log.EmbeddedBatch(_logger, batch.Count);
        return results;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Embedded batch of {Count}")]
        public static partial void EmbeddedBatch(ILogger logger, int count);
    }
}
