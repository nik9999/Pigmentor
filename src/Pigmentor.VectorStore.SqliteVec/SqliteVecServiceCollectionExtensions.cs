using Microsoft.Extensions.DependencyInjection;
using Pigmentor.Core.Abstractions;

namespace Pigmentor.VectorStore.SqliteVec;

/// <summary>
/// Dependency injection extensions for registering <see cref="SqliteVecStore"/>.
/// </summary>
public static class SqliteVecServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SqliteVecStore"/> as the singleton <see cref="IVectorStore"/> implementation.
    /// </summary>
    public static IServiceCollection AddSqliteVecStore(
        this IServiceCollection services,
        Action<SqliteVecStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new SqliteVecStoreOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<IVectorStore>(sp => new SqliteVecStore(options));
        services.AddSingleton<SqliteVecStore>(sp => (SqliteVecStore)sp.GetRequiredService<IVectorStore>());

        return services;
    }

    /// <summary>
    /// Registers <see cref="SqliteVecStore"/> as the singleton <see cref="IVectorStore"/> implementation
    /// with the specified connection string, vector dimensions, and embedding model.
    /// </summary>
    public static IServiceCollection AddSqliteVecStore(
        this IServiceCollection services,
        string connectionString,
        int dimensions,
        string embeddingModel)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentException.ThrowIfNullOrWhiteSpace(embeddingModel);

        return services.AddSqliteVecStore(opts =>
        {
            opts.ConnectionString = connectionString;
            opts.Dimensions = dimensions;
            opts.EmbeddingModel = embeddingModel;
        });
    }
}
