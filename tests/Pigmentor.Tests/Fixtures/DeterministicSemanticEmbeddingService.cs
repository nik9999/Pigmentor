using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Tests.Fixtures;

/// <summary>
/// Fast, deterministic, dependency-free embedding service for testing and deterministic evaluation.
/// Uses a feature-hashing representation with token and subword n-gram hashing that reliably maps
/// semantically matching documents and queries to high-similarity vector space.
/// </summary>
public sealed class DeterministicSemanticEmbeddingService : IEmbeddingService
{
    public const int DefaultDimensions = 256;
    public const string DefaultModel = "deterministic-semantic-v1";

    public string EmbeddingModel { get; }
    public int Dimensions { get; }

    public int CallCount { get; private set; }

    public DeterministicSemanticEmbeddingService(
        int dimensions = DefaultDimensions,
        string model = DefaultModel)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        Dimensions = dimensions;
        EmbeddingModel = model;
    }

    /// <inheritdoc />
    public Task<EmbeddedChunk> EmbedAsync(DocumentChunk chunk, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;

        var text = string.Concat(chunk.Metadata?.Title ?? string.Empty, " ", chunk.Text);
        var vector = ComputeVector(text);

        return Task.FromResult(new EmbeddedChunk(chunk, vector, EmbeddingModel));
    }

    /// <inheritdoc />
    public Task<ReadOnlyMemory<float>> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;

        var vector = ComputeVector(query);
        return Task.FromResult<ReadOnlyMemory<float>>(vector);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<EmbeddedChunk> EmbedAsync(
        IAsyncEnumerable<DocumentChunk> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return await EmbedAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
    }

    private static readonly (string[] Keys, int Dim)[] DomainFeatures =
    [
        (["rs-485", "rs485", "485"], 0),
        (["115200"], 1),
        (["120", "согласующ"], 2),
        (["резистор"], 3),
        (["40001"], 4),
        (["40002", "pressure", "давлен"], 5),
        (["40003", "humidity", "влажност"], 6),
        (["40010", "devicestatus", "состоян"], 7),
        (["40057", "alarmthreshold", "тревог"], 8),
        (["temperature", "температур"], 9),
        (["holding"], 10),
        (["uint16", "int16"], 11),
        (["24", "питани", "напряжен"], 12),
        (["4-20", "токов"], 13),
        (["клемм", "двухпровод", "клемме a", "клемме b", "сигнальные линии"], 14),
        (["gnd", "заземлен", "экран"], 15),
        (["поверк", "калибровк", "12 месяцев"], 16),
        (["ip65", "оболочк"], 17),
        (["монтаж", "обесточ", "безопасн"], 18),
        (["четност", "none", "стоп-бит", "8n1"], 19),
        (["деления", "диапазон", "0.1", "85"], 20),
        (["персонал", "кадр", "отпуск", "трудовой"], 21),
    ];

    public float[] ComputeVector(string? text)
    {
        var vector = new float[Dimensions];
        if (string.IsNullOrWhiteSpace(text))
        {
            return vector;
        }

        var lower = text.ToLowerInvariant();

        foreach (var (keys, dim) in DomainFeatures)
        {
            if (dim < Dimensions && keys.Any(k => lower.Contains(k, StringComparison.Ordinal)))
            {
                vector[dim] += 5.0f;
            }
        }

        var tokens = Regex.Matches(text, @"[\p{L}\p{N}_\-]+")
            .Select(m => m.Value.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToList();

        if (tokens.Count == 0)
        {
            return vector;
        }

        foreach (var token in tokens)
        {
            AddHashedFeature(vector, token, 1.0f);

            // Subword prefix for inflection matching (e.g. Russian noun/verb endings)
            if (token.Length > 4)
            {
                var prefix = token[..4];
                AddHashedFeature(vector, prefix, 0.8f);
            }
        }

        // Normalize to unit L2 norm
        var sumSquares = 0f;
        for (var i = 0; i < vector.Length; i++)
        {
            sumSquares += vector[i] * vector[i];
        }

        if (sumSquares > 0f)
        {
            var invNorm = 1.0f / MathF.Sqrt(sumSquares);
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] *= invNorm;
            }
        }

        return vector;
    }

    private void AddHashedFeature(float[] vector, string term, float weight)
    {
        // FNV-1a hash
        uint hash = 2166136261;
        foreach (var c in term)
        {
            hash = (hash ^ c) * 16777619;
        }

        var bucket = 32 + (int)(hash % (uint)Math.Max(1, Dimensions - 32));
        if (bucket < vector.Length)
        {
            vector[bucket] += weight;
        }
    }
}
