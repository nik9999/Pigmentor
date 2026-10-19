using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
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
    /// </summary>
    public static PigmentorBuilder UseConfiguredProviders(this PigmentorBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton<IChatClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LlmClientOptions>>().Value;
            return options.Provider switch
            {
                LlmProvider.Ollama => new OllamaApiClient(new Uri(options.Endpoint), options.ChatModel),

                LlmProvider.GigaChat => new GigaChatClient(new GigaChatClientOptions
                {
                    AuthorizationKey = options.AuthorizationKey,
                    BaseUrl = new Uri(options.Endpoint),
                    DefaultModel = options.ChatModel,
                    DefaultEmbeddingModel = "Embeddings"
                }).GetChatClient(options.ChatModel),

                _ => throw new InvalidOperationException($"Unsupported LLM provider: {options.Provider}"),
            };
        });

        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EmbeddingClientOptions>>().Value;
            IEmbeddingGenerator<string, Embedding<float>> inner = options.Provider switch
            {
                EmbeddingProvider.Ollama => new OllamaApiClient(new Uri(options.Endpoint), options.Model),

                EmbeddingProvider.Onnx => throw new NotSupportedException(
                    "The ONNX embedding backend is not registered yet. Register a local ONNX IEmbeddingGenerator implementation for Pigmentor:Embedding:ModelPath."),

                _ => throw new InvalidOperationException($"Unsupported embedding provider: {options.Provider}"),
            };

            return new EmbeddingGenerator(inner, options.Model, options.Dimensions);
        });

        return builder;
    }
}

