namespace Pigmentor.App.Security;

/// <summary>
/// A secret store implementation used when no secure OS-level credential store is available.
/// Strictly fails closed to prevent silent plaintext fallback.
/// </summary>
public sealed class FailClosedSecretStore : ISecretStore
{
    private readonly string _reason;

    public FailClosedSecretStore(string? reason = null)
    {
        _reason = string.IsNullOrWhiteSpace(reason)
            ? "Secure credential store is unavailable on this platform. Use environment variable PIGMENTOR_GIGACHAT_AUTH_KEY or configure a supported credential store."
            : reason;
    }

    public ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        // Safe to return null on GetAsync so resolver can continue checking other sources (e.g. environment variable)
        return ValueTask.FromResult<string?>(null);
    }

    public ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        throw new PlatformNotSupportedException(_reason);
    }

    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        return ValueTask.CompletedTask;
    }
}

