using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.DependencyInjection;
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
        services.TryAddSingleton<IReranker, NoOpReranker>();
        services.TryAddTransient<IContextBuilder, ContextBuilder>();
        services.TryAddTransient<IRagPipeline, RagPipeline>();
        services.TryAddTransient<RagPipeline>();

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
