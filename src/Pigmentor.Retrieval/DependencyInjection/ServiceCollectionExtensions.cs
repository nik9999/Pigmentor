using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Pigmentor.Core.Abstractions;
using Pigmentor.Retrieval.Decorators;
using Pigmentor.Retrieval.VectorStores;

namespace Pigmentor.Retrieval.DependencyInjection;

/// <summary>
/// DI registration extensions for the Pigmentor retrieval abstractions.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory <see cref="IVectorStore"/> (brute-force cosine).
    /// The shared test/dev/Hello-World backend; no external dependency.
    /// </summary>
    public static IServiceCollection AddPigmentorInMemoryVectorStore(
        this IServiceCollection services,
        string collectionName = "in-memory")
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        services.AddSingleton<IVectorStore>(_ => new InMemoryVectorStore(collectionName));
        return services;
    }

    /// <summary>
    /// Wraps the supplied <paramref name="inner"/> retriever in one of the classic-RAG
    /// enhancement decorators selected by <paramref name="enhancement"/>,
    /// and registers the result as an <see cref="IRetriever"/>. Each decorator
    /// needs a registered <see cref="IChatClient"/> (resolved from the container):
    /// <list type="bullet">
    ///   <item><description><c>hyde</c> → <see cref="HydeRetriever"/> (hypothetical-document embeddings).</description></item>
    ///   <item><description><c>rag-fusion</c> → <see cref="RagFusionRetriever"/> (multi-query + RRF).</description></item>
    ///   <item><description><c>crag</c> → <see cref="CragRetriever"/> (corrective grading + optional web fallback).</description></item>
    ///   <item><description><c>stepback</c> → <see cref="StepBackRetriever"/> (step-back abstraction + RRF).</description></item>
    /// </list>
    /// Matching is case-insensitive; an unrecognized value throws
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <param name="services">The DI container being built.</param>
    /// <param name="enhancement">One of <c>hyde</c>, <c>rag-fusion</c>, <c>crag</c>, or <c>stepback</c> (case-insensitive).</param>
    /// <param name="inner">A factory for the inner retriever the enhancement decorates.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    /// <exception cref="InvalidOperationException">The <paramref name="enhancement"/> is not a known strategy.</exception>
    public static IServiceCollection AddPigmentorEnhancementRetriever(
        this IServiceCollection services,
        string enhancement,
        Func<IServiceProvider, IRetriever> inner)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(enhancement);
        ArgumentNullException.ThrowIfNull(inner);

        services.AddSingleton<IRetriever>(sp =>
        {
            var chat = sp.GetRequiredService<IChatClient>();
            return enhancement.Trim().ToLowerInvariant() switch
            {
                "hyde" => new HydeRetriever(inner(sp), chat),
                "rag-fusion" => new RagFusionRetriever(inner(sp), chat),
                "crag" => new CragRetriever(inner(sp), chat),
                "stepback" => new StepBackRetriever(inner(sp), chat),
                _ => throw new InvalidOperationException(
                    $"Unknown enhancement '{enhancement}'. Expected 'hyde', 'rag-fusion', 'crag', or 'stepback'."),
            };
        });

        return services;
    }
}
