using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Pigmentor.Core.Configuration;

/// <summary>DI wiring for the <see cref="IFeatureGate"/> seam (Ch 25).</summary>
public static class FeatureGateRegistration
{
    /// <summary>
    /// Register the configuration-backed <see cref="IFeatureGate"/> at request
    /// scope. The <c>IConfiguration</c> the host already registers is the backing
    /// store; in a managed deployment that configuration is layered with Azure App
    /// Configuration so flags flip centrally — no code change here.
    /// </summary>
    /// <param name="services">The DI service collection.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddFeatureGate(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IFeatureGate, ConfigurationFeatureGate>();
        return services;
    }
}
