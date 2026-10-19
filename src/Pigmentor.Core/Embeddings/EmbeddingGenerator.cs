using Microsoft.Extensions.AI;

namespace Pigmentor.Core.Embeddings;

/// <summary>
/// Pigmentor-owned embedding generator facade. Provider-specific generators
/// (Ollama today, ONNX next) are kept behind the common AI abstraction while
/// model identity and vector dimensions remain available to the application.
/// </summary>
public sealed class EmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>, IDisposable
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _inner;
    private readonly bool _disposeInner;
    private bool _disposed;

    public EmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> inner,
        string modelId,
        int dimensions = 0,
        bool disposeInner = true)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentOutOfRangeException.ThrowIfNegative(dimensions);

        ModelId = modelId;
        Dimensions = dimensions;
        _disposeInner = disposeInner;
        Metadata = new EmbeddingGeneratorMetadata("pigmentor", null, modelId);
    }

    public string ModelId { get; }

    public int Dimensions { get; private set; }

    public EmbeddingGeneratorMetadata Metadata { get; }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(values);

        var result = await _inner.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
        if (Dimensions == 0 && result.Count > 0)
        {
            Dimensions = result[0].Vector.Length;
        }

        if (Dimensions > 0 && result.Any(item => item.Vector.Length != Dimensions))
        {
            throw new InvalidOperationException(
                $"Embedding provider returned a vector with unexpected dimensions. Expected {Dimensions}.");
        }

        return result;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is null)
        {
            if (serviceType == typeof(EmbeddingGeneratorMetadata))
            {
                return Metadata;
            }

            if (serviceType.IsInstanceOfType(this))
            {
                return this;
            }

            return _inner.GetService(serviceType);
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_disposeInner && _inner is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _disposed = true;
    }
}
