namespace Pigmentor.App.Security;

/// <summary>
/// Abstraction for invoking the Linux secret-tool CLI (FreeDesktop Secret Service API).
/// Enables deterministic testing without requiring a desktop D-Bus session during unit tests.
/// </summary>
public interface ILinuxSecretToolRunner
{
    /// <summary>
    /// Checks whether secret-tool is installed and responsive on the system.
    /// </summary>
    bool IsAvailable();

    /// <summary>
    /// Securely stores a secret via secret-tool without passing credentials in CLI arguments.
    /// </summary>
    Task StoreSecretAsync(string label, string service, string key, string secret, CancellationToken cancellationToken);

    /// <summary>
    /// Looks up a secret by service and key attributes. Returns null if not found.
    /// </summary>
    Task<string?> LookupSecretAsync(string service, string key, CancellationToken cancellationToken);

    /// <summary>
    /// Clears a secret matching the service and key attributes.
    /// </summary>
    Task ClearSecretAsync(string service, string key, CancellationToken cancellationToken);
}

