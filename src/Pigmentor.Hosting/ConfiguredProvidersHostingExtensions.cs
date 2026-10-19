using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OllamaSharp;
using Pigmentor.Core.Configuration;
using Pigmentor.Core.Embeddings;
using Pigmentor.GigaChat;

namespace Pigmentor.Hosting;

/// <summary>
/// Hosting extensions for resolving providers based on application configuration.
/// </summary>
public static class ConfiguredProvidersHostingExtensions
{
    /// <summary>
    /// Configures chat and embedding providers dynamically based on bound
    /// <see cref="LlmClientOptions.Provider"/> and <see cref="EmbeddingClientOptions.Provider"/>.
    /// Overwrites previously registered clients ("last explicit registration wins").
    /// </summary>
    public static PigmentorBuilder UseConfiguredProviders(this PigmentorBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.RemoveAll<IChatClient>();
        builder.Services.AddSingleton<GigaChatClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LlmClientOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.AuthorizationKey))
            {
                throw new InvalidOperationException("Pigmentor:Llm:AuthorizationKey is required when Provider is GigaChat.");
            }

            return new GigaChatClient(new GigaChatClientOptions
            {
                AuthorizationKey = options.AuthorizationKey,
                BaseUrl = new Uri(options.Endpoint),
                DefaultModel = options.ChatModel
            });
        });
        builder.Services.AddSingleton<IChatClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LlmClientOptions>>().Value;

            if (string.IsNullOrWhiteSpace(options.Endpoint) || !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpointUri))
            {
                throw new InvalidOperationException($"Pigmentor:Llm:Endpoint is invalid or not an absolute URI: '{options.Endpoint}'.");
            }

            if (string.IsNullOrWhiteSpace(options.ChatModel))
            {
                throw new InvalidOperationException("Pigmentor:Llm:ChatModel is required.");
            }

            return options.Provider switch
            {
                LlmProvider.Ollama => new OllamaApiClient(endpointUri, options.ChatModel),

                LlmProvider.GigaChat => sp.GetRequiredService<GigaChatClient>().GetChatClient(options.ChatModel),

                _ => throw new InvalidOperationException($"Unsupported LLM provider: {options.Provider}"),
            };
        });

        builder.Services.RemoveAll<IEmbeddingGenerator<string, Embedding<float>>>();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EmbeddingClientOptions>>().Value;
            IEmbeddingGenerator<string, Embedding<float>> inner;

            switch (options.Provider)
            {
                case EmbeddingProvider.Ollama:
                    if (string.IsNullOrWhiteSpace(options.Endpoint) || !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var embUri))
                    {
                        throw new InvalidOperationException($"Pigmentor:Embedding:Endpoint is invalid or not an absolute URI: '{options.Endpoint}'.");
                    }
                    if (string.IsNullOrWhiteSpace(options.Model))
                    {
                        throw new InvalidOperationException("Pigmentor:Embedding:Model is required when Provider is Ollama.");
                    }
                    if (options.Dimensions < 0)
                    {
                        throw new InvalidOperationException("Pigmentor:Embedding:Dimensions cannot be negative.");
                    }
                    inner = new OllamaApiClient(embUri, options.Model);
                    break;

                case EmbeddingProvider.Onnx:
                    if (string.IsNullOrWhiteSpace(options.ModelPath))
                    {
                        throw new InvalidOperationException("Pigmentor:Embedding:ModelPath is required when Provider is Onnx.");
                    }
                    if (options.Dimensions <= 0)
                    {
                        throw new InvalidOperationException("Pigmentor:Embedding:Dimensions must be greater than zero when Provider is Onnx.");
                    }
                    throw new NotSupportedException(
                        "The ONNX embedding backend is not registered yet. Register a local ONNX IEmbeddingGenerator implementation for Pigmentor:Embedding:ModelPath.");

                default:
                    throw new InvalidOperationException($"Unsupported embedding provider: {options.Provider}");
            }

            return new EmbeddingGenerator(inner, options.Model, options.Dimensions);
        });

        return builder;
    }

}
