using Microsoft.Extensions.Configuration;

namespace Pigmentor.Core.Configuration;

/// <summary>
/// <see cref="IFeatureGate"/> backed by <see cref="IConfiguration"/> (Ch 25). Flag
/// and value lookups are namespaced under a single <c>Features</c> section, so a
/// flag <c>reranking.enabled</c> resolves to the configuration path
/// <c>Features:reranking.enabled</c>. Because it reads <see cref="IConfiguration"/>,
/// it observes reloads automatically: with a reloading provider (file watcher,
/// Azure App Configuration refresh) a flipped flag is seen on the next read with
/// no restart.
///
/// <para>
/// Registered at request scope so each request reads a consistent snapshot while
/// still picking up configuration that changed between requests.
/// </para>
/// </summary>
public sealed class ConfigurationFeatureGate : IFeatureGate
{
    /// <summary>The configuration section all flags and values are read from.</summary>
    public const string SectionName = "Features";

    private readonly IConfigurationSection _features;

    /// <summary>Create the gate over the application's <paramref name="configuration"/>.</summary>
    /// <param name="configuration">The configuration root; the <c>Features</c> section is read from it.</param>
    public ConfigurationFeatureGate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _features = configuration.GetSection(SectionName);
    }

    /// <inheritdoc />
    public bool IsEnabled(string flag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flag);
        // Fail-closed: absent or non-boolean values read as false.
        return _features.GetValue<bool>(flag);
    }

    /// <inheritdoc />
    public T GetValue<T>(string key, T defaultValue = default!)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        // GetValue returns the bound value or the supplied default; the explicit
        // coalesce satisfies nullable flow analysis for reference-type T.
        return _features.GetValue(key, defaultValue) ?? defaultValue;
    }
}
