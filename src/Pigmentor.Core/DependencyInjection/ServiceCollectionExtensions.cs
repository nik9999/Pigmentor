using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OllamaSharp;
using Pigmentor.Core.Configuration;
using Pigmentor.Core.Embeddings;
using Pigmentor.Core.Tokens;
using Pigmentor.GigaChat;

namespace Pigmentor.Core.DependencyInjection;

/// <summary>
/// DI registration extensions for Pigmentor.Core. The seed of the
/// <c>services.AddPigmentorRagPipeline()</c> NuGet pattern that ships in
/// Chapter 25.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Wire up the Pigmentor core services: <see cref="LlmClientOptions"/>,
    /// <see cref="IChatClient"/>, <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>,
    /// and <see cref="ITokenCounter"/>.
    /// </summary>
    /// <param name="services">The DI container being built.</param>
    /// <param name="configuration">
    /// Application configuration. The <c>Pigmentor:Llm</c> section is bound
    /// to <see cref="LlmClientOptions"/>; the active provider determines
    /// which client implementation is registered.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddPigmentorCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<LlmClientOptions>()
            .Bind(configuration.GetSection(LlmClientOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                static opts => opts.Provider != LlmProvider.GigaChat || !string.IsNullOrWhiteSpace(opts.AuthorizationKey),
                "Pigmentor:Llm:AuthorizationKey is required when Provider is GigaChat.")
            .ValidateOnStart();

        services.AddOptions<EmbeddingClientOptions>()
            .Bind(configuration.GetSection(EmbeddingClientOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                static opts => opts.Provider != EmbeddingProvider.Onnx || !string.IsNullOrWhiteSpace(opts.ModelPath),
                "Pigmentor:Embedding:ModelPath is required when Provider is Onnx.")
            .ValidateOnStart();

        services.AddSingleton<IChatClient>(BuildChatClient);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<EmbeddingClientOptions>>().Value;
            return new EmbeddingGenerator(
                BuildEmbeddingGenerator(sp),
                options.Model,
                options.Dimensions);
        });
        services.AddSingleton<ITokenCounter>(_ => new TokenCounter());

        return services;
    }

    private static IChatClient BuildChatClient(IServiceProvider sp)
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

            _ => throw new InvalidOperationException($"Unsupported provider: {options.Provider}"),
        };
    }

    private static OllamaApiClient BuildEmbeddingGenerator(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<EmbeddingClientOptions>>().Value;
        return options.Provider switch
        {
            EmbeddingProvider.Ollama => new OllamaApiClient(new Uri(options.Endpoint), options.Model),

            EmbeddingProvider.Onnx => throw new NotSupportedException(
                "The ONNX embedding backend is not registered yet. Register a local ONNX IEmbeddingGenerator implementation for Pigmentor:Embedding:ModelPath."),

            _ => throw new InvalidOperationException($"Unsupported embedding provider: {options.Provider}"),
        };
    }

}
