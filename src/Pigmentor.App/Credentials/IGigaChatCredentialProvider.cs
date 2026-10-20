namespace Pigmentor.App.Credentials;

/// <summary>
/// Source where a credential was resolved from.
/// </summary>
public enum CredentialSource
{
    None = 0,
    Explicit = 1,
    Environment = 2,
    SecretStore = 3
}

/// <summary>
/// Resolution result describing credential availability and source without exposing the key.
/// </summary>
public sealed record CredentialResolutionResult(string? Key, CredentialSource Source)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Key);

    public string SourceDescription => Source switch
    {
        CredentialSource.Explicit => "explicit runtime configuration",
        CredentialSource.Environment => "environment variable (PIGMENTOR_GIGACHAT_AUTH_KEY)",
        CredentialSource.SecretStore => "OS protected secret store",
        _ => "not configured"
    };
}

/// <summary>
/// Provider abstraction for resolving GigaChat credentials across explicit, environment, and secure store sources.
/// </summary>
public interface IGigaChatCredentialProvider
{
    /// <summary>
    /// Returns the active GigaChat Authorization Key or null if not configured.
    /// </summary>
    ValueTask<string?> GetAuthorizationKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves credential status and its provenance source.
    /// </summary>
    ValueTask<CredentialResolutionResult> ResolveCredentialAsync(CancellationToken cancellationToken = default);
}

