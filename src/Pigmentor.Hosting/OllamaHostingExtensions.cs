using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OllamaSharp;
using Pigmentor.Core.Configuration;
using Pigmentor.Core.Embeddings;

namespace Pigmentor.Hosting;

/// <summary>
/// Hosting extensions for configuring Ollama as an LLM and/or embedding provider.
/// </summary>
public static class OllamaHostingExtensions
{
    /// <summary>
    /// Configures Ollama for both chat and embedding generation using options from configuration or defaults.
    /// </summary>
    public static PigmentorBuilder UseOllama(
        this PigmentorBuilder builder,
        Action<LlmClientOptions>? configureChat = null,
        Action<EmbeddingClientOptions>? configureEmbedding = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseOllamaChat(configureChat);
        builder.UseOllamaEmbeddings(configureEmbedding);

        return builder;
    }

    /// <summary>
    /// Configures Ollama for both chat and embedding generation with explicit endpoint and model identifiers.
    /// </summary>
    public static PigmentorBuilder UseOllama(
        this PigmentorBuilder builder,
        Uri endpoint,
        string chatModel = "llama3.2",
        string embeddingModel = "nomic-embed-text")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModel);

        builder.UseOllamaChat(opts =>
        {
            opts.Endpoint = endpoint.ToString();
            opts.ChatModel = chatModel;
        });

        builder.UseOllamaEmbeddings(opts =>
        {
            opts.Endpoint = endpoint.ToString();
            opts.Model = embeddingModel;
        });

        return builder;
    }

    /// <summary>
    /// Configures Ollama as the primary <see cref="IChatClient"/> provider.
    /// Overwrites any previously registered <see cref="IChatClient"/> ("last explicit registration wins").
    /// </summary>
    public static PigmentorBuilder UseOllamaChat(
        this PigmentorBuilder builder,
        Action<LlmClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        builder.Services.RemoveAll<IChatClient>();
        builder.Services.AddSingleton<IChatClient>(sp =>
        {
            var options = sp.GetService<IOptions<LlmClientOptions>>()?.Value ?? new LlmClientOptions();
            if (string.IsNullOrWhiteSpace(options.Endpoint) || !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint))
            {
                throw new InvalidOperationException($"Ollama chat endpoint is invalid or not an absolute URI: '{options.Endpoint}'.");
            }
            var model = string.IsNullOrWhiteSpace(options.ChatModel) ? "llama3.2" : options.ChatModel;
            return new OllamaApiClient(endpoint, model);
        });

        return builder;
    }

    /// <summary>
    /// Configures Ollama as the primary <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> provider.
    /// Overwrites any previously registered <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> ("last explicit registration wins").
    /// </summary>
    public static PigmentorBuilder UseOllamaEmbeddings(
        this PigmentorBuilder builder,
        Action<EmbeddingClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        builder.Services.RemoveAll<IEmbeddingGenerator<string, Embedding<float>>>();
        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var options = sp.GetService<IOptions<EmbeddingClientOptions>>()?.Value ?? new EmbeddingClientOptions();
            if (string.IsNullOrWhiteSpace(options.Endpoint) || !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint))
            {
                throw new InvalidOperationException($"Ollama embedding endpoint is invalid or not an absolute URI: '{options.Endpoint}'.");
            }
            if (options.Dimensions < 0)
            {
                throw new InvalidOperationException("Ollama embedding dimensions cannot be negative.");
            }
            var model = string.IsNullOrWhiteSpace(options.Model) ? "nomic-embed-text" : options.Model;
            var client = new OllamaApiClient(endpoint, model);
            return new EmbeddingGenerator(client, model, options.Dimensions);
        });

        return builder;
    }
}

