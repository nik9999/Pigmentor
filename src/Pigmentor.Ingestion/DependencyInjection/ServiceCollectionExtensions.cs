using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Configuration;
using Pigmentor.Ingestion.Chunking;
using Pigmentor.Ingestion.Extraction;
using Pigmentor.Ingestion.Normalization;
using Pigmentor.Ingestion.Services;

namespace Pigmentor.Ingestion.DependencyInjection;

/// <summary>
/// DI registration extensions for the Pigmentor ingestion pipeline. Lives in
/// <c>Pigmentor.Ingestion</c> (rather than <c>Pigmentor.Core</c>) because the
/// concrete chunkers it wires up — <see cref="RecursiveCharacterChunker"/> and
/// <see cref="ContextualChunker"/> — are defined here, and <c>Pigmentor.Core</c>
/// must not take a dependency on <c>Pigmentor.Ingestion</c>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Bind <see cref="PigmentorOptions"/> to the <c>Pigmentor</c> configuration
    /// section and register the default <see cref="IChunker"/>. The registration
    /// reads <see cref="IngestionOptions.MaxChunkSize"/> for the recursive budget
    /// and, when <see cref="IngestionOptions.UseContextualRetrieval"/> is set,
    /// wraps the recursive chunker in a <see cref="ContextualChunker"/> (which
    /// needs the registered <see cref="IChatClient"/> at index time).
    /// </summary>
    /// <param name="services">The DI container being built.</param>
    /// <param name="configuration">
    /// Application configuration. The <c>Pigmentor</c> section is bound to
    /// <see cref="PigmentorOptions"/>; its <c>Ingestion</c> sub-section drives the
    /// chunker selection.
    /// </param>
    /// <returns>The same <paramref name="services"/> instance, for chaining.</returns>
    public static IServiceCollection AddPigmentorIngestion(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PigmentorOptions>()
            .Bind(configuration.GetSection(PigmentorOptions.SectionName));

        services.AddSingleton<IChunker>(static sp =>
        {
            var opts = sp.GetRequiredService<IOptions<PigmentorOptions>>().Value;
            var inner = new RecursiveCharacterChunker(maxChunkSize: opts.Ingestion.MaxChunkSize);
            if (!opts.Ingestion.UseContextualRetrieval)
            {
                return inner;
            }

            var chat = sp.GetRequiredService<IChatClient>();
            return new ContextualChunker(inner, chat);
        });

        return services;
    }

    /// <summary>
    /// Registers <see cref="DocumentIngestionService"/> as the singleton <see cref="IDocumentIngestionService"/> implementation.
    /// Requires <see cref="IIngestionStateStore"/>, <see cref="IVectorStore"/>, <see cref="IChunker"/>, and <see cref="IEmbeddingService"/> to be registered.
    /// </summary>
    public static IServiceCollection AddDocumentIngestionService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IDocumentIngestionService, DocumentIngestionService>();
        return services;
    }

    /// <summary>
    /// Registers document extraction and normalization services.
    /// </summary>
    public static IServiceCollection AddDocumentExtraction(
        this IServiceCollection services,
        Action<ExtractionLimits>? configureLimits = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var limits = new ExtractionLimits();
        configureLimits?.Invoke(limits);

        services.AddSingleton(limits);
        services.AddSingleton<IOcrTableDetector, OcrTableDetector>();
        services.AddSingleton<IDocumentNormalizer, DocumentNormalizer>();
        services.AddSingleton<IDocumentExtractor>(sp =>
        {
            var ocr = sp.GetService<IOcrEngine>();
            var tableDetector = sp.GetService<IOcrTableDetector>();
            var lim = sp.GetService<ExtractionLimits>() ?? limits;
            return new DocumentExtractor(ocrEngine: ocr, ocrTableDetector: tableDetector, limits: lim);
        });

        return services;
    }
}
