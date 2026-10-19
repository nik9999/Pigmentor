using System.Collections.Concurrent;

namespace Pigmentor.Core.Conversations;

/// <summary>
/// Process-local <see cref="IConversationStateStore"/> — the default for the dev
/// inner loop and the one the unit tests run against (Ch 25). State lives in a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> with a per-entry absolute
/// expiry; an entry past its TTL is treated as absent and lazily evicted on the
/// next access. Time is read from an injected <see cref="TimeProvider"/> so a
/// test can advance the clock and assert expiry deterministically.
///
/// <para>
/// This is intentionally <em>not</em> the production hot store — a single
/// process's memory is not shared across instances and is lost on restart.
/// Production swaps in the Redis-backed
/// <c>DistributedConversationStateStore</c> (Pigmentor.Performance) without any
/// caller change. Both honor the same TTL-as-retention-boundary contract.
/// </para>
/// </summary>
public sealed class InMemoryConversationStateStore : IConversationStateStore
{
    private readonly record struct Entry(string State, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    /// <summary>Create the store, optionally over a test <see cref="TimeProvider"/>.</summary>
    /// <param name="timeProvider">The clock used for TTL expiry. Defaults to <see cref="TimeProvider.System"/>.</param>
    public InMemoryConversationStateStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task<string?> GetAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        cancellationToken.ThrowIfCancellationRequested();

        if (_entries.TryGetValue(conversationId, out var entry))
        {
            if (_timeProvider.GetUtcNow() < entry.ExpiresAt)
            {
                return Task.FromResult<string?>(entry.State);
            }

            // Past TTL: lazily evict so memory does not grow unbounded with stale
            // sessions. Remove only if the snapshot we read is still the live one.
            _entries.TryRemove(new KeyValuePair<string, Entry>(conversationId, entry));
        }

        return Task.FromResult<string?>(null);
    }

    /// <inheritdoc />
    public Task SetAsync(
        string conversationId,
        string state,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        var expiresAt = _timeProvider.GetUtcNow() + ttl;
        _entries[conversationId] = new Entry(state, expiresAt);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        cancellationToken.ThrowIfCancellationRequested();

        _entries.TryRemove(conversationId, out _);
        return Task.CompletedTask;
    }
}
