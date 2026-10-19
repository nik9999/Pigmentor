using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pigmentor.Core.Configuration;
using Pigmentor.Core.Conversations;
using Pigmentor.Core.Tokens;

namespace Pigmentor.Core.DependencyInjection;

/// <summary>
/// DI registration extensions for provider-independent Pigmentor.Core services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Wire up provider-independent Pigmentor core services: <see cref="ITokenCounter"/>,
    /// <see cref="IConversationStateStore"/>, and optionally bind configuration options
    /// (<see cref="LlmClientOptions"/> and <see cref="EmbeddingClientOptions"/>).
    /// </summary>
    /// <param name="services">The DI container being built.</param>
    /// <param name="configuration">
    /// Optional application configuration to bind <see cref="LlmClientOptions"/> and
    /// <see cref="EmbeddingClientOptions"/>.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddPigmentorCore(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configuration is not null)
        {
            services.AddOptions<LlmClientOptions>()
                .Bind(configuration.GetSection(LlmClientOptions.SectionName))
                .ValidateDataAnnotations();

            services.AddOptions<EmbeddingClientOptions>()
                .Bind(configuration.GetSection(EmbeddingClientOptions.SectionName))
                .ValidateDataAnnotations();
        }

        services.TryAddSingleton<ITokenCounter>(_ => new TokenCounter());
        services.TryAddSingleton<IConversationStateStore, InMemoryConversationStateStore>();

        return services;
    }
}
