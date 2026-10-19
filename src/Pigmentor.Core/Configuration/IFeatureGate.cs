namespace Pigmentor.Core.Configuration;

/// <summary>
/// A minimal runtime feature-flag / configuration seam (Ch 25 §"Feature flags /
/// runtime config"). Lets a request-time branch — turn reranking on, switch
/// retrieval strategy, route to a different model — be toggled by configuration
/// rather than a redeploy. Two members: a boolean flag check and a typed value
/// read.
///
/// <para>
/// The shipped implementation (<see cref="ConfigurationFeatureGate"/>) reads from
/// <c>IConfiguration</c> under a <c>Features</c> section, which is enough for the
/// book's purposes and works offline with zero infrastructure. In a managed
/// deployment the same seam is backed by <strong>Azure App Configuration</strong>
/// with its feature-management store — flags flip centrally and propagate to every
/// instance without a redeploy, and percentage/targeting filters become available
/// (the Ch 25 Bicep provisions the App Configuration resource). That swap happens
/// behind this interface: <c>Microsoft.FeatureManagement</c> /
/// <c>Microsoft.Azure.AppConfiguration.AspNetCore</c> are deliberately NOT
/// referenced here, keeping the core package dependency-free.
/// </para>
/// </summary>
public interface IFeatureGate
{
    /// <summary>
    /// Whether the boolean flag <paramref name="flag"/> is enabled. Unknown flags
    /// are treated as disabled (fail-closed), so a missing flag never silently
    /// turns a feature on.
    /// </summary>
    /// <param name="flag">The flag name, e.g. <c>reranking.enabled</c>.</param>
    bool IsEnabled(string flag);

    /// <summary>
    /// Read a typed configuration value at <paramref name="key"/>, returning
    /// <paramref name="defaultValue"/> when the key is absent or cannot be
    /// converted to <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The value type to bind to.</typeparam>
    /// <param name="key">The value key, e.g. <c>routing.model</c>.</param>
    /// <param name="defaultValue">The fallback when the key is missing or unbindable.</param>
    T GetValue<T>(string key, T defaultValue = default!);
}
