using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pigmentor.Core.Configuration;
using Pigmentor.Core.Embeddings;
using Pigmentor.GigaChat;

namespace Pigmentor.Hosting;

/// <summary>
/// Hosting extensions for configuring Sber GigaChat as an LLM and/or embedding provider.
/// </summary>
public static class GigaChatHostingExtensions
{
    /// <summary>
    /// Configures GigaChat for both chat and embedding generation using options from configuration or callback.
    /// </summary>
    public static PigmentorBuilder UseGigaChat(
        this PigmentorBuilder builder,
        Action<GigaChatClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseGigaChatChat(configure);
        builder.UseGigaChatEmbeddings();

        return builder;
    }

    /// <summary>
    /// Configures GigaChat for both chat and embedding generation with an explicit authorization key and model identifiers.
    /// </summary>
    public static PigmentorBuilder UseGigaChat(
        this PigmentorBuilder builder,
        string authorizationKey,
        string? chatModel = null,
        string? embeddingModel = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationKey);

        return builder.UseGigaChat(opts =>
        {
            opts.AuthorizationKey = authorizationKey;
            if (!string.IsNullOrWhiteSpace(chatModel))
            {
                opts.DefaultModel = chatModel;
            }
            if (!string.IsNullOrWhiteSpace(embeddingModel))
            {
                opts.DefaultEmbeddingModel = embeddingModel;
            }
        });
    }

    /// <summary>
    /// Configures GigaChat as the primary <see cref="IChatClient"/> provider.
    /// </summary>
    public static PigmentorBuilder UseGigaChatChat(
        this PigmentorBuilder builder,
        Action<GigaChatClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        EnsureGigaChatClientRegistered(builder.Services, configure);

        builder.Services.AddSingleton<IChatClient>(sp =>
        {
            var gigaChat = sp.GetRequiredService<GigaChatClient>();
            var llmOpts = sp.GetService<IOptions<LlmClientOptions>>()?.Value;
            var model = !string.IsNullOrWhiteSpace(llmOpts?.ChatModel) ? llmOpts.ChatModel : gigaChat.Options.DefaultModel;
            return gigaChat.GetChatClient(model);
        });

        return builder;
    }

    /// <summary>
    /// Configures GigaChat as the primary <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> provider.
    /// </summary>
    public static PigmentorBuilder UseGigaChatEmbeddings(
        this PigmentorBuilder builder,
        Action<GigaChatClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        EnsureGigaChatClientRegistered(builder.Services, configure);

        builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var gigaChat = sp.GetRequiredService<GigaChatClient>();
            var embOpts = sp.GetService<IOptions<EmbeddingClientOptions>>()?.Value;
            var model = !string.IsNullOrWhiteSpace(embOpts?.Model) ? embOpts.Model : gigaChat.Options.DefaultEmbeddingModel;
            var inner = gigaChat.GetEmbeddingGenerator(model);
            return new EmbeddingGenerator(inner, model, embOpts?.Dimensions ?? 0);
        });

        return builder;
    }

    private static void EnsureGigaChatClientRegistered(
        IServiceCollection services,
        Action<GigaChatClientOptions>? configure)
    {
        services.AddOptions<GigaChatClientOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<GigaChatClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<GigaChatClientOptions>>().Value;

            // Fallback to Pigmentor:Llm options if AuthorizationKey is not set on GigaChatClientOptions
            if (string.IsNullOrWhiteSpace(options.AuthorizationKey))
            {
                var llmOpts = sp.GetService<IOptions<LlmClientOptions>>()?.Value;
                if (!string.IsNullOrWhiteSpace(llmOpts?.AuthorizationKey))
                {
                    options.AuthorizationKey = llmOpts.AuthorizationKey;
                }
                if (!string.IsNullOrWhiteSpace(llmOpts?.Endpoint) && !llmOpts.Endpoint.Contains("localhost"))
                {
                    options.BaseUrl = new Uri(llmOpts.Endpoint);
                }
                if (!string.IsNullOrWhiteSpace(llmOpts?.ChatModel))
                {
                    options.DefaultModel = llmOpts.ChatModel;
                }
            }

            return new GigaChatClient(options);
        });
    }
}

