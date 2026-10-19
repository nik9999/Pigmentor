using Azure;
using Azure.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Pigmentor.Core.Abstractions;
using Pigmentor.Retrieval.Decorators;
using Pigmentor.Retrieval.Hybrid;
using Pigmentor.Retrieval.VectorStores;

namespace Pigmentor.Retrieval.DependencyInjection;

/// <summary>
/// DI registration extensions for the Pigmentor <see cref="IVectorStore"/>
/// adapters. These give the tidy one-liner experience the chapter promises
/// while keeping the hand-rolled domain port as the abstraction. The names are
/// Pigmentor-scoped on purpose — they register <see cref="IVectorStore"/>
/// (our port), not <c>Microsoft.Extensions.VectorData</c> types.
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
    /// Registers the Qdrant-backed <see cref="IVectorStore"/> over a gRPC
    /// <see cref="QdrantClient"/> built from <paramref name="host"/> /
    /// <paramref name="port"/> (gRPC default 6334).
    /// </summary>
    /// <param name="services">The DI container being built.</param>
    /// <param name="collectionName">The Qdrant collection backing the store.</param>
    /// <param name="vectorSize">Embedding dimensionality.</param>
    /// <param name="host">Qdrant host (default <c>localhost</c>).</param>
    /// <param name="port">Qdrant gRPC port (default 6334).</param>
    /// <param name="distance">Similarity metric (default cosine).</param>
    /// <param name="useScalarQuantization">
    /// Opt in to int8 scalar quantization at collection creation (off by
    /// default — the baseline collection layout is unchanged).
    /// </param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddPigmentorQdrantVectorStore(
        this IServiceCollection services,
        string collectionName,
        int vectorSize,
        string host = "localhost",
        int port = 6334,
        Distance distance = Distance.Cosine,
        bool useScalarQuantization = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vectorSize);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        services.AddSingleton<IVectorStore>(_ =>
            new QdrantVectorStore(
                new QdrantClient(host, port),
                collectionName,
                vectorSize,
                distance,
                useScalarQuantization));
        return services;
    }

    /// <summary>
    /// Registers the Azure AI Search-backed <see cref="IVectorStore"/> using an
    /// admin <see cref="AzureKeyCredential"/>.
    /// </summary>
    public static IServiceCollection AddPigmentorAzureAiSearchVectorStore(
        this IServiceCollection services,
        Uri endpoint,
        string indexName,
        int vectorSize,
        AzureKeyCredential credential)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vectorSize);
        ArgumentNullException.ThrowIfNull(credential);

        services.AddSingleton<IVectorStore>(_ =>
            new AzureAiSearchVectorStore(endpoint, indexName, vectorSize, credential));
        return services;
    }

    /// <summary>
    /// Registers the Azure AI Search-backed <see cref="IVectorStore"/> using an
    /// Entra ID <see cref="TokenCredential"/> (e.g. managed identity).
    /// </summary>
    public static IServiceCollection AddPigmentorAzureAiSearchVectorStore(
        this IServiceCollection services,
        Uri endpoint,
        string indexName,
        int vectorSize,
        TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vectorSize);
        ArgumentNullException.ThrowIfNull(credential);

        services.AddSingleton<IVectorStore>(_ =>
            new AzureAiSearchVectorStore(endpoint, indexName, vectorSize, credential));
        return services;
    }

    /// <summary>
    /// Registers a single-store hybrid <see cref="IRetriever"/> (Chapter 14)
    /// selected by <paramref name="backend"/>. Each backend's database performs
    /// dense + sparse + RRF fusion internally:
    /// <list type="bullet">
    ///   <item><description><c>postgres</c> → <see cref="PostgresHybridRetriever"/> (pgvector <c>&lt;=&gt;</c> + FTS <c>ts_rank_cd</c>, RRF-in-SQL).</description></item>
    ///   <item><description><c>qdrant</c> → <see cref="QdrantHybridRetriever"/> (Query API prefetch legs fused with <c>Fusion.Rrf</c>).</description></item>
    ///   <item><description><c>azure</c> → <see cref="AzureAiSearchHybridRetriever"/> (vector + keyword fused by Azure's RRF).</description></item>
    /// </list>
    /// The <see cref="IEmbeddingService"/> used to embed the query is resolved
    /// from the container, so register one first. Backend matching is
    /// case-insensitive; an unrecognized value throws
    /// <see cref="InvalidOperationException"/>. Composable with the
    /// <c>AddPigmentor…VectorStore</c> registrations — this adds the hybrid
    /// <see cref="IRetriever"/>, it does not replace any <see cref="IVectorStore"/>.
    /// </summary>
    /// <param name="services">The DI container being built.</param>
    /// <param name="backend">One of <c>postgres</c>, <c>qdrant</c>, or <c>azure</c> (case-insensitive).</param>
    /// <param name="options">The backend connection configuration. Exactly one backend's fields are read.</param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    /// <exception cref="InvalidOperationException">The <paramref name="backend"/> is not a known backend, or its required options are missing.</exception>
    public static IServiceCollection AddPigmentorHybridRetriever(
        this IServiceCollection services,
        string backend,
        HybridRetrieverOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(backend);
        ArgumentNullException.ThrowIfNull(options);

        switch (backend.Trim().ToLowerInvariant())
        {
            case "postgres":
                var connectionString = options.PostgresConnectionString
                    ?? throw new InvalidOperationException(
                        "Hybrid backend 'postgres' requires HybridRetrieverOptions.PostgresConnectionString.");
                services.AddSingleton<IRetriever>(sp =>
                    new PostgresHybridRetriever(
                        sp.GetRequiredService<IEmbeddingService>(),
                        connectionString,
                        options.PostgresOptions));
                break;

            case "qdrant":
                services.AddSingleton<IRetriever>(sp =>
                    new QdrantHybridRetriever(
                        new QdrantClient(options.QdrantHost, options.QdrantPort),
                        sp.GetRequiredService<IEmbeddingService>(),
                        options.QdrantCollectionName ?? throw new InvalidOperationException(
                            "Hybrid backend 'qdrant' requires HybridRetrieverOptions.QdrantCollectionName."),
                        options.QdrantDenseVectorName,
                        options.QdrantSparseVectorName));
                break;

            case "azure":
                var endpoint = options.AzureEndpoint
                    ?? throw new InvalidOperationException(
                        "Hybrid backend 'azure' requires HybridRetrieverOptions.AzureEndpoint.");
                var indexName = options.AzureIndexName
                    ?? throw new InvalidOperationException(
                        "Hybrid backend 'azure' requires HybridRetrieverOptions.AzureIndexName.");
                if (options.AzureTokenCredential is { } tokenCredential)
                {
                    services.AddSingleton<IRetriever>(sp =>
                        new AzureAiSearchHybridRetriever(
                            endpoint, indexName, tokenCredential,
                            sp.GetRequiredService<IEmbeddingService>(),
                            semanticConfigurationName: options.AzureSemanticConfigurationName));
                }
                else if (options.AzureKeyCredential is { } keyCredential)
                {
                    services.AddSingleton<IRetriever>(sp =>
                        new AzureAiSearchHybridRetriever(
                            endpoint, indexName, keyCredential,
                            sp.GetRequiredService<IEmbeddingService>(),
                            semanticConfigurationName: options.AzureSemanticConfigurationName));
                }
                else
                {
                    throw new InvalidOperationException(
                        "Hybrid backend 'azure' requires either AzureKeyCredential or AzureTokenCredential.");
                }
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown hybrid backend '{backend}'. Expected 'postgres', 'qdrant', or 'azure'.");
        }

        return services;
    }

    /// <summary>
    /// Wraps the supplied <paramref name="inner"/> retriever in one of the Ch 15
    /// classic-RAG enhancement decorators selected by <paramref name="enhancement"/>,
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

/// <summary>
/// Backend connection configuration for
/// <see cref="ServiceCollectionExtensions.AddPigmentorHybridRetriever"/>. Only
/// the fields belonging to the selected backend are read; the rest are ignored.
/// </summary>
public sealed class HybridRetrieverOptions
{
    // ── postgres ──────────────────────────────────────────────────────────
    /// <summary>The Npgsql connection string for the <c>postgres</c> backend.</summary>
    public string? PostgresConnectionString { get; init; }

    /// <summary>Optional table/column configuration for the <c>postgres</c> backend.</summary>
    public PostgresHybridOptions? PostgresOptions { get; init; }

    // ── qdrant ────────────────────────────────────────────────────────────
    /// <summary>Qdrant host for the <c>qdrant</c> backend. Default <c>localhost</c>.</summary>
    public string QdrantHost { get; init; } = "localhost";

    /// <summary>Qdrant gRPC port for the <c>qdrant</c> backend. Default 6334.</summary>
    public int QdrantPort { get; init; } = 6334;

    /// <summary>The hybrid collection name for the <c>qdrant</c> backend.</summary>
    public string? QdrantCollectionName { get; init; }

    /// <summary>The named dense vector for the <c>qdrant</c> backend. Default <c>dense</c>.</summary>
    public string QdrantDenseVectorName { get; init; } = "dense";

    /// <summary>The named sparse (IDF-modified) vector for the <c>qdrant</c> backend. Default <c>sparse</c>.</summary>
    public string QdrantSparseVectorName { get; init; } = "sparse";

    // ── azure ─────────────────────────────────────────────────────────────
    /// <summary>The Azure AI Search endpoint for the <c>azure</c> backend.</summary>
    public Uri? AzureEndpoint { get; init; }

    /// <summary>The index name for the <c>azure</c> backend.</summary>
    public string? AzureIndexName { get; init; }

    /// <summary>An admin/query key for the <c>azure</c> backend. Mutually exclusive with <see cref="AzureTokenCredential"/>.</summary>
    public AzureKeyCredential? AzureKeyCredential { get; init; }

    /// <summary>An Entra ID credential for the <c>azure</c> backend. Takes precedence over <see cref="AzureKeyCredential"/>.</summary>
    public TokenCredential? AzureTokenCredential { get; init; }

    /// <summary>Optional semantic-configuration name for the <c>azure</c> backend (off when <see langword="null"/>).</summary>
    public string? AzureSemanticConfigurationName { get; init; }
}
