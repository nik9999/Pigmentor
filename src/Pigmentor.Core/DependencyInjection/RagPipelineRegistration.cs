using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Pigmentor.Core.DependencyInjection;

/// <summary>
/// Capstone-time NuGet shape from Ch 25:
///
/// <code>
/// services.AddPigmentorRagPipeline(options =>
/// {
///     options.UseQdrant(qdrantConnString);
///     options.UseNeo4j(neo4jConnString);
///     options.UseAzureOpenAI(apiKey, endpoint);
///     options.EnableReranking(cohereApiKey);
///     options.EnableCaching(redisConnString);
/// });
/// </code>
///
/// The actual implementations of <c>UseXxx</c> live in the corresponding
/// project's <c>ServiceCollectionExtensions</c>; this class is the stitching
/// point so a consumer needs only one extension method.
/// </summary>
public static class RagPipelineRegistration
{
    public static IServiceCollection AddPigmentorRagPipeline(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<RagPipelineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new RagPipelineOptions();
        configure?.Invoke(options);

        services.AddPigmentorCore(configuration);
        // Phase 7 wiring is intentionally minimal here. A real consumer
        // composes additional services from Pigmentor.Retrieval,
        // Pigmentor.Reranking, Pigmentor.Generation, Pigmentor.Performance,
        // Pigmentor.Security, and Pigmentor.Operations as the options
        // dictate. The pattern is shown in samples/Ch25_Capstone (when it
        // ships in Phase 8 polish).

        return services;
    }
}

/// <summary>Capstone-time options surface. Each Use*/Enable* method sets a flag the registration reads.</summary>
public sealed class RagPipelineOptions
{
    public string? QdrantConnectionString { get; private set; }
    public string? Neo4jConnectionString { get; private set; }
    public string? RedisConnectionString { get; private set; }
    public bool RerankingEnabled { get; private set; }
    public bool AuditingEnabled { get; private set; }

    public RagPipelineOptions UseQdrant(string connectionString)
    {
        QdrantConnectionString = connectionString;
        return this;
    }

    public RagPipelineOptions UseNeo4j(string connectionString)
    {
        Neo4jConnectionString = connectionString;
        return this;
    }

    public RagPipelineOptions EnableCaching(string redisConnectionString)
    {
        RedisConnectionString = redisConnectionString;
        return this;
    }

    public RagPipelineOptions EnableReranking()
    {
        RerankingEnabled = true;
        return this;
    }

    public RagPipelineOptions EnableAuditing()
    {
        AuditingEnabled = true;
        return this;
    }
}
