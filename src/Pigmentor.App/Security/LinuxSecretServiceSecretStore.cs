namespace Pigmentor.App.Security;

/// <summary>
/// Linux FreeDesktop Secret Service implementation of <see cref="ISecretStore"/> (e.g. GNOME Keyring).
/// Uses `secret-tool` over D-Bus without writing plaintext secrets to disk.
/// Fails closed if Secret Service daemon is unavailable.
/// </summary>
public sealed class LinuxSecretServiceSecretStore : ISecretStore
{
    private const string ServiceName = "pigmentor";
    private readonly ILinuxSecretToolRunner _runner;

    public LinuxSecretServiceSecretStore(ILinuxSecretToolRunner? runner = null)
    {
        _runner = runner ?? new ProcessLinuxSecretToolRunner();
    }

    public async ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_runner.IsAvailable())
        {
            return null; // Return null to allow fallback to environment variable
        }

        try
        {
            return await _runner.LookupSecretAsync(ServiceName, name, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_runner.IsAvailable())
        {
            throw new PlatformNotSupportedException(
                "Secure Linux Secret Service storage is unavailable. Ensure 'libsecret-tools' (secret-tool) and a running Secret Service daemon (such as GNOME Keyring) are available, or use the environment variable PIGMENTOR_GIGACHAT_AUTH_KEY.");
        }

        var label = $"Pigmentor: {name}";
        await _runner.StoreSecretAsync(label, ServiceName, name, value, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_runner.IsAvailable())
        {
            return;
        }

        try
        {
            await _runner.ClearSecretAsync(ServiceName, name, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Best effort deletion
        }
    }
}

