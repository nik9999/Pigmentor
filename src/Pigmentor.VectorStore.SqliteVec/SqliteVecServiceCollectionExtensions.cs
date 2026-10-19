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
    /// with the specified connection string and dimensions.
    /// </summary>
    public static IServiceCollection AddSqliteVecStore(
        this IServiceCollection services,
        string connectionString,
        int dimensions = 0)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return services.AddSqliteVecStore(opts =>
        {
            opts.ConnectionString = connectionString;
            opts.Dimensions = dimensions;
        });
    }
}

