using System;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Pigmentor.GigaChat.Extensions;

/// <summary>
/// Service collection extensions for registering Sber GigaChat services and Microsoft.Extensions.AI adapters in DI.
/// </summary>
public static class GigaChatServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="GigaChatClient"/> and options in the dependency injection container.
    /// </summary>
    public static IServiceCollection AddGigaChatClient(
        this IServiceCollection services,
        Action<GigaChatClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<GigaChatClientOptions>();
        if (configure != null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<GigaChatClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<GigaChatClientOptions>>().Value;
            return new GigaChatClient(options);
        });

        return services;
    }

    /// <summary>
    /// Registers <see cref="GigaChatClient"/> bound from configuration section (defaults to "GigaChat").
    /// </summary>
    public static IServiceCollection AddGigaChatClient(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = GigaChatClientOptions.SectionName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<GigaChatClientOptions>(configuration.GetSection(sectionName));

        services.TryAddSingleton<GigaChatClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<GigaChatClientOptions>>().Value;
            return new GigaChatClient(options);
        });

        return services;
    }

    /// <summary>
    /// Registers GigaChat as the primary <see cref="IChatClient"/> in the DI container.
    /// </summary>
    public static IServiceCollection AddGigaChatChatClient(
        this IServiceCollection services,
        string? modelId = null,
        Action<GigaChatClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddGigaChatClient(configure);
        services.TryAddSingleton<IChatClient>(sp =>
        {
            var client = sp.GetRequiredService<GigaChatClient>();
            return client.GetChatClient(modelId);
        });

        return services;
    }

    /// <summary>
    /// Registers GigaChat as the primary <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> in the DI container.
    /// </summary>
    public static IServiceCollection AddGigaChatEmbeddingGenerator(
        this IServiceCollection services,
        string? modelId = null,
        Action<GigaChatClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddGigaChatClient(configure);
        services.TryAddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var client = sp.GetRequiredService<GigaChatClient>();
            return client.GetEmbeddingGenerator(modelId);
        });

        return services;
    }

    /// <summary>
    /// Configures a <see cref="ChatClientBuilder"/> to route calls through a <see cref="GigaChatClient"/>.
    /// </summary>
    public static ChatClientBuilder UseGigaChat(
        this ChatClientBuilder builder,
        GigaChatClient client,
        string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(client);

        return builder.Use(_ => client.GetChatClient(modelId));
    }
}
