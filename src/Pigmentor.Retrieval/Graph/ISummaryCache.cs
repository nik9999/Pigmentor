using System.Security.Cryptography;
using System.Text;

namespace Pigmentor.Retrieval.Graph;

/// <summary>
/// Cache for query-time subgraph summaries — the seam that makes the
/// LazyGraphRAG economics real. The expensive part of LazyGraphRAG is the
/// per-query summarization LLM call; caching it so that semantically
/// equivalent queries (those that resolve to the <em>same</em> subgraph)
/// reuse one summary is what keeps the per-query cost bounded.
/// </summary>
/// <remarks>
/// The cache key is derived internally from the SORTED subgraph entity ids,
/// so two different natural-language queries that traverse to the same set of
/// entities hit the same entry regardless of id order.
/// </remarks>
public interface ISummaryCache
{
    /// <summary>
    /// Return the cached summary for the subgraph identified by
    /// <paramref name="subgraphEntityIds"/>, or <see langword="null"/> when
    /// absent or expired. Records a hit or a miss against
    /// <see cref="Hits"/> / <see cref="Misses"/>.
    /// </summary>
    Task<string?> TryGetAsync(IReadOnlyCollection<string> subgraphEntityIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Store <paramref name="summary"/> for the subgraph identified by
    /// <paramref name="subgraphEntityIds"/>.
    /// </summary>
    Task SetAsync(IReadOnlyCollection<string> subgraphEntityIds, string summary, CancellationToken cancellationToken = default);

    /// <summary>
    /// Evict every cached summary whose subgraph included
    /// <paramref name="entityId"/>. Call this when an entity's facts change so
    /// that stale summaries built from it are not served — see the invalidation
    /// discussion in Chapter 17.
    /// </summary>
    Task EvictByEntityAsync(string entityId, CancellationToken cancellationToken = default);

    /// <summary>Total cache hits observed by <see cref="TryGetAsync"/>.</summary>
    long Hits { get; }

    /// <summary>Total cache misses (absent or expired) observed by <see cref="TryGetAsync"/>.</summary>
    long Misses { get; }
}

/// <summary>
/// In-memory <see cref="ISummaryCache"/> for the development inner loop and
/// unit tests. It computes a STABLE key from the ordinal-sorted entity ids
/// (SHA-256 hex — never <see cref="string.GetHashCode()"/>, which is randomized
/// per process), stores <c>(summary, expiresAt)</c> with a TTL, and maintains an
/// <c>entityId → set&lt;key&gt;</c> reverse index so
/// <see cref="EvictByEntityAsync"/> can drop every summary built from a changed
/// entity.
/// </summary>
/// <remarks>
/// The production adapter is an <c>IDistributedCache</c> / Redis implementation
/// (Chapter 21) so summaries survive process restarts and are shared across
/// instances; this in-memory variant is the dev/test seam with the same
/// contract.
/// </remarks>
public sealed class InMemorySummaryCache : ISummaryCache
{
    private readonly TimeSpan _ttl;
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _entityToKeys = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private long _hits;
    private long _misses;

    /// <summary>Create the cache with an optional TTL (default 7 days).</summary>
    /// <param name="ttl">
    /// How long a cached summary stays valid. Defaults to 7 days when
    /// <see langword="null"/>; the production Redis adapter uses the same TTL
    /// concept via key expiry.
    /// </param>
    public InMemorySummaryCache(TimeSpan? ttl = null)
    {
        _ttl = ttl ?? TimeSpan.FromDays(7);
    }

    /// <inheritdoc />
    public long Hits => Interlocked.Read(ref _hits);

    /// <inheritdoc />
    public long Misses => Interlocked.Read(ref _misses);

    /// <inheritdoc />
    public Task<string?> TryGetAsync(IReadOnlyCollection<string> subgraphEntityIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subgraphEntityIds);
        var key = ComputeKey(subgraphEntityIds);

        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    Interlocked.Increment(ref _hits);
                    return Task.FromResult<string?>(entry.Summary);
                }

                // Expired — treat as a miss and clean up eagerly.
                RemoveKeyLocked(key);
            }
        }

        Interlocked.Increment(ref _misses);
        return Task.FromResult<string?>(null);
    }

    /// <inheritdoc />
    public Task SetAsync(IReadOnlyCollection<string> subgraphEntityIds, string summary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subgraphEntityIds);
        ArgumentNullException.ThrowIfNull(summary);
        var ids = Normalize(subgraphEntityIds);
        var key = ComputeKey(ids);

        lock (_lock)
        {
            _entries[key] = new CacheEntry(summary, DateTimeOffset.UtcNow.Add(_ttl));
            foreach (var id in ids)
            {
                if (!_entityToKeys.TryGetValue(id, out var keys))
                {
                    keys = new HashSet<string>(StringComparer.Ordinal);
                    _entityToKeys[id] = keys;
                }
                keys.Add(key);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task EvictByEntityAsync(string entityId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        lock (_lock)
        {
            if (_entityToKeys.TryGetValue(entityId, out var keys))
            {
                // Snapshot: RemoveKeyLocked mutates _entityToKeys as it prunes
                // the reverse index for every member entity of each key.
                foreach (var key in keys.ToArray())
                {
                    RemoveKeyLocked(key);
                }
                _entityToKeys.Remove(entityId);
            }
        }

        return Task.CompletedTask;
    }

    // Drop a single cache entry and scrub it from every entity's reverse index.
    private void RemoveKeyLocked(string key)
    {
        _entries.Remove(key);
        foreach (var (entityId, keys) in _entityToKeys)
        {
            keys.Remove(key);
            _ = entityId;
        }
    }

    private static IReadOnlyList<string> Normalize(IReadOnlyCollection<string> subgraphEntityIds) =>
        [.. subgraphEntityIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)];

    private static string ComputeKey(IReadOnlyCollection<string> subgraphEntityIds)
    {
        // Stable, process-independent key over the ordinal-sorted ids. SHA-256
        // hex (NOT string.GetHashCode, which is randomized per process) so the
        // same subgraph maps to the same key across runs and instances.
        var ids = subgraphEntityIds is IReadOnlyList<string> normalized && IsSortedDistinct(normalized)
            ? normalized
            : Normalize(subgraphEntityIds);
        var joined = string.Join("\0", ids);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexStringLower(bytes);
    }

    private static bool IsSortedDistinct(IReadOnlyList<string> ids)
    {
        for (var i = 1; i < ids.Count; i++)
        {
            if (string.CompareOrdinal(ids[i - 1], ids[i]) >= 0)
            {
                return false;
            }
        }
        return true;
    }

    private readonly record struct CacheEntry(string Summary, DateTimeOffset ExpiresAt);
}
