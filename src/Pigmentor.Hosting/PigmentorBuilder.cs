using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
}

