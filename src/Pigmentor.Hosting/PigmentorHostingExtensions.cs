using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.DependencyInjection;
using Pigmentor.Core.Tokens;
using Pigmentor.Generation;
using Pigmentor.Retrieval;

namespace Pigmentor.Hosting;

/// <summary>
/// Top-level service registration extensions for Pigmentor.
/// </summary>
public static class PigmentorHostingExtensions
{
    /// <summary>
    /// Registers Pigmentor core services, pipeline abstractions, and returns a <see cref="PigmentorBuilder"/>
    /// for configuring providers and pipeline stages.
    /// </summary>
    /// <param name="services">The DI container being built.</param>
    /// <param name="configuration">Optional application configuration.</param>
    /// <returns>A <see cref="PigmentorBuilder"/> for chaining provider registrations.</returns>
    public static PigmentorBuilder AddPigmentor(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddPigmentorCore(configuration);

        if (configuration != null)
        {
            services.Configure<RagPipelineSettings>(configuration.GetSection("Pigmentor:Rag"));
        }

        services.TryAddSingleton<IReranker, NoOpReranker>();
        services.TryAddTransient<IContextBuilder, ContextBuilder>();
        services.TryAddTransient<IRagPipeline>(sp =>
        {
            var retriever = sp.GetRequiredService<IRetriever>();
            var chatGenerator = sp.GetService<IChatGenerator>();
            var chatClient = chatGenerator == null ? sp.GetRequiredService<IChatClient>() : null;
            var reranker = sp.GetService<IReranker>();
            var contextBuilder = sp.GetService<IContextBuilder>();
            var tokenCounter = sp.GetService<ITokenCounter>();
            var settings = sp.GetService<IOptions<RagPipelineSettings>>()?.Value;
            var rrfMerger = sp.GetService<Pigmentor.Retrieval.RrfMerger>();

            return chatGenerator != null
                ? new RagPipeline(retriever, chatGenerator, reranker, contextBuilder, tokenCounter, settings, rrfMerger)
                : new RagPipeline(retriever, chatClient!, reranker, contextBuilder, tokenCounter, settings, rrfMerger);
        });
        services.TryAddTransient<RagPipeline>(sp => (RagPipeline)sp.GetRequiredService<IRagPipeline>());

        return new PigmentorBuilder(services, configuration);
    }

    /// <summary>
    /// Legacy registration API for the Pigmentor RAG pipeline.
    /// Prefer <see cref="AddPigmentor(IServiceCollection, IConfiguration?)"/> and explicit builder extensions.
    /// </summary>
    [Obsolete("Use services.AddPigmentor(configuration) instead. RagPipelineOptions does not configure backend stores.")]
    public static PigmentorBuilder AddPigmentorRagPipeline(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<RagPipelineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddPigmentor(configuration);
    }
}
