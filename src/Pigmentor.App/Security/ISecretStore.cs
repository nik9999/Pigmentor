namespace Pigmentor.App.Security;

/// <summary>
/// Reusable abstraction for OS-protected or secure credential storage.
/// Secrets stored here are never stored in plain text.
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Retrieves a secret by name. Returns null if not found.
    /// </summary>
    ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Securely persists or updates a secret by name.
    /// </summary>
    ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a secret by name. No-op if the secret does not exist.
    /// </summary>
    ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default);
}

