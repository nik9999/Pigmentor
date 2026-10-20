using System.Collections.Concurrent;

namespace Pigmentor.App.Security;

/// <summary>
/// Thread-safe in-memory secret store intended for tests and transient scenarios.
/// </summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _store = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask<string?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _store.TryGetValue(name, out var value);
        return ValueTask.FromResult(value);
    }

    public ValueTask SetAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        _store[name] = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _store.TryRemove(name, out _);
        return ValueTask.CompletedTask;
    }
}

