using Pigmentor.App.Security;

namespace Pigmentor.App.Credentials;

/// <summary>
/// Deterministic credential resolver implementing the strict resolution precedence:
/// 1. Explicit runtime credential
/// 2. Environment variable (PIGMENTOR_GIGACHAT_AUTH_KEY)
/// 3. OS Secret Store (gigachat_auth_key)
/// 4. Not configured (None)
/// </summary>
public sealed class GigaChatCredentialResolver : IGigaChatCredentialProvider
{
    public const string EnvironmentVariableName = "PIGMENTOR_GIGACHAT_AUTH_KEY";
    public const string SecretStoreKeyName = "gigachat_auth_key";

    private readonly ISecretStore _secretStore;
    private readonly string? _explicitKey;
    private readonly Func<string, string?> _envResolver;

    public GigaChatCredentialResolver(
        ISecretStore secretStore,
        string? explicitKey = null,
        Func<string, string?>? envResolver = null)
    {
        _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));
        _explicitKey = string.IsNullOrWhiteSpace(explicitKey) ? null : explicitKey.Trim();
        _envResolver = envResolver ?? (k => Environment.GetEnvironmentVariable(k));
    }

    public async ValueTask<string?> GetAuthorizationKeyAsync(CancellationToken cancellationToken = default)
    {
        var result = await ResolveCredentialAsync(cancellationToken).ConfigureAwait(false);
        return result.Key;
    }

    public async ValueTask<CredentialResolutionResult> ResolveCredentialAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Explicit runtime credential
        if (!string.IsNullOrWhiteSpace(_explicitKey))
        {
            return new CredentialResolutionResult(_explicitKey, CredentialSource.Explicit);
        }

        // 2. Environment variable
        var envValue = _envResolver(EnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            return new CredentialResolutionResult(envValue.Trim(), CredentialSource.Environment);
        }

        // 3. OS Secret Store
        try
        {
            var storedKey = await _secretStore.GetAsync(SecretStoreKeyName, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(storedKey))
            {
                return new CredentialResolutionResult(storedKey.Trim(), CredentialSource.SecretStore);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PlatformNotSupportedException)
        {
            // Headless / unsupported platform falls through to None
        }

        // 4. Not configured
        return new CredentialResolutionResult(null, CredentialSource.None);
    }
}
