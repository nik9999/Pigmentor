using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pigmentor.Core.Abstractions;
using Pigmentor.Generation;
using Pigmentor.Ingestion.DependencyInjection;

namespace Pigmentor.Hosting;

/// <summary>
/// Fluent builder for configuring Pigmentor services and AI providers.
/// </summary>
public sealed class PigmentorBuilder
{
    /// <summary>
    /// The application service collection being configured.
    /// </summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Optional application configuration associated with this builder.
    /// </summary>
    public IConfiguration? Configuration { get; }

    /// <summary>
    /// Initializes a new instance of <see cref="PigmentorBuilder"/>.
    /// </summary>
    public PigmentorBuilder(IServiceCollection services, IConfiguration? configuration = null)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Configuration = configuration;
    }

    /// <summary>
    /// Configures runtime settings for <see cref="IRagPipeline"/>.
    /// </summary>
    /// <param name="configure">Delegate to configure <see cref="RagPipelineSettings"/>.</param>
    /// <returns>This builder for chaining.</returns>
    public PigmentorBuilder ConfigureRagPipeline(Action<RagPipelineSettings> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure(configure);
        return this;
    }

    /// <summary>
    /// Registers document extraction and normalization services for PDF, Word, Excel, and images.
    /// </summary>
    /// <param name="configureLimits">Optional delegate to customize extraction resource limits.</param>
    /// <returns>This builder for chaining.</returns>
    public PigmentorBuilder AddDocumentExtraction(Action<ExtractionLimits>? configureLimits = null)
    {
        Services.AddDocumentExtraction(configureLimits);
        return this;
    }

    /// <summary>
    /// Registers an OCR engine implementation for image and scanned PDF processing.
    /// </summary>
    public PigmentorBuilder UseOcrEngine<TOcrEngine>() where TOcrEngine : class, IOcrEngine
    {
        Services.AddSingleton<IOcrEngine, TOcrEngine>();
        return this;
    }

    /// <summary>
    /// Registers an OCR engine instance for image and scanned PDF processing.
    /// </summary>
    public PigmentorBuilder UseOcrEngine(IOcrEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        Services.AddSingleton(engine);
        return this;
    }
}
