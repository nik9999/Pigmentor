using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Pigmentor.GigaChat.Model;

namespace Pigmentor.GigaChat;

/// <summary>
/// <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> implementation wrapping Sber GigaChat embeddings API.
/// </summary>
public sealed class GigaChatEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly GigaChatClient _client;
    private readonly string _defaultModelId;
    private readonly bool _disposeClient;
    private bool _disposed;

    public EmbeddingGeneratorMetadata Metadata { get; }

    public GigaChatEmbeddingGenerator(GigaChatClient client, string? defaultModelId = null, bool disposeClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _defaultModelId = defaultModelId ?? client.Options.DefaultEmbeddingModel;
        _disposeClient = disposeClient;

        Metadata = new EmbeddingGeneratorMetadata(
            "gigachat",
            _client.Options.BaseUrl,
            _defaultModelId);
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        ThrowIfDisposed();

        var inputList = values as IList<string> ?? values.ToList();
        if (inputList.Count == 0)
        {
            return new GeneratedEmbeddings<Embedding<float>>([]);
        }

        var model = options?.ModelId ?? _defaultModelId;
        var request = new GigaChatEmbeddingsRequest
        {
            Model = model,
            Input = inputList
        };

        var response = await _client.GenerateEmbeddingsAsync(request, cancellationToken).ConfigureAwait(false);

        var embeddings = response.Data
            .OrderBy(d => d.Index)
            .Select(d => new Embedding<float>(new ReadOnlyMemory<float>(d.Embedding)))
            .ToList();

        UsageDetails? usage = null;
        if (response.Usage != null)
        {
            usage = new UsageDetails
            {
                InputTokenCount = response.Usage.PromptTokens,
                TotalTokenCount = response.Usage.TotalTokens
            };
        }

        return new GeneratedEmbeddings<Embedding<float>>(embeddings)
        {
            Usage = usage
        };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey == null)
        {
            if (serviceType == typeof(EmbeddingGeneratorMetadata))
            {
                return Metadata;
            }

            if (serviceType.IsInstanceOfType(this))
            {
                return this;
            }

            if (serviceType.IsInstanceOfType(_client))
            {
                return _client;
            }
        }

        return null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_disposeClient)
            {
                _client.Dispose();
            }
            _disposed = true;
        }
    }
}
