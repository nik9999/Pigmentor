using System;
using Microsoft.Extensions.AI;

namespace Pigmentor.GigaChat.Extensions;

/// <summary>
/// Fluent extension methods for converting <see cref="GigaChatClient"/> and options into standard Microsoft.Extensions.AI abstractions.
/// </summary>
public static class GigaChatClientExtensions
{
    /// <summary>
    /// Adapts the <see cref="GigaChatClient"/> into an <see cref="IChatClient"/>.
    /// </summary>
    /// <param name="client">The underlying GigaChat client.</param>
    /// <param name="modelId">Optional target model ID (e.g. <see cref="GigaChatModels.GigaChat"/>, <see cref="GigaChatModels.GigaChatPro"/>).</param>
    public static IChatClient AsChatClient(this GigaChatClient client, string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new GigaChatChatClient(client, modelId);
    }

    /// <summary>
    /// Adapts the <see cref="GigaChatClient"/> into an <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>.
    /// </summary>
    /// <param name="client">The underlying GigaChat client.</param>
    /// <param name="modelId">Optional target embedding model ID (e.g. <see cref="GigaChatModels.Embeddings"/>).</param>
    public static IEmbeddingGenerator<string, Embedding<float>> AsEmbeddingGenerator(this GigaChatClient client, string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new GigaChatEmbeddingGenerator(client, modelId);
    }

    /// <summary>
    /// Creates and configures a standalone <see cref="IChatClient"/> from options.
    /// </summary>
    public static IChatClient AsChatClient(this GigaChatClientOptions options, string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var client = new GigaChatClient(options);
        return new GigaChatChatClient(client, modelId, disposeClient: true);
    }

    /// <summary>
    /// Creates and configures a standalone <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> from options.
    /// </summary>
    public static IEmbeddingGenerator<string, Embedding<float>> AsEmbeddingGenerator(this GigaChatClientOptions options, string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var client = new GigaChatClient(options);
        return new GigaChatEmbeddingGenerator(client, modelId, disposeClient: true);
    }
}
