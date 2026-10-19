using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pigmentor.Core.Abstractions;

namespace Pigmentor.Ingestion.Events;

/// <summary>
/// A <see cref="BackgroundService"/> that drains an <see cref="IChangeFeed"/> and,
/// for each <see cref="DocumentChanged"/> event, re-ingests the document and
/// invalidates the caches that depend on it (Ch 22). Three invariants the chapter
/// names are enforced per event before any work runs:
///
/// <list type="bullet">
///   <item><description><strong>Tenant-scoped</strong> — when a tenant filter is configured,
///   events for other tenants are ignored, so one tenant's churn never re-ingests
///   another's documents.</description></item>
///   <item><description><strong>Version-guarded ordering</strong> — an event whose
///   <see cref="DocumentChanged.Version"/> is at or below the last version processed
///   for that document is a stale / out-of-order delivery and is skipped.</description></item>
///   <item><description><strong>Idempotent</strong> — a duplicate delivery of an
///   already-processed (document, version) pair is a no-op (the version guard treats
///   "equal" as "already done").</description></item>
/// </list>
///
/// <para>
/// On success the event is acked so the broker stops redelivering it; on failure it
/// is left unacked and surfaced for retry. The re-ingest and cache-invalidation
/// entry points are injected (the in-process <see cref="DocumentReingestService"/>
/// and a cache-invalidation callback) so the consumer runs offline in tests.
/// </para>
/// </summary>
public sealed partial class IngestEventConsumer : BackgroundService
{
    private readonly IChangeFeed _feed;
    private readonly IDocumentReingestService _reingest;
    private readonly Func<string, CancellationToken, Task> _invalidateCache;
    private readonly string? _tenantId;
    private readonly ILogger<IngestEventConsumer> _logger;
    private readonly IIngestionStateStore? _stateStore;

    // documentId -> highest version processed. Serves as in-process fast cache.
    private readonly ConcurrentDictionary<string, long> _lastVersion = new(StringComparer.Ordinal);

    /// <summary>
    /// Create the consumer.
    /// </summary>
    /// <param name="feed">The change feed to drain.</param>
    /// <param name="reingest">The re-ingest entry point (delete-then-reinsert).</param>
    /// <param name="invalidateCache">
    /// The cache-invalidation entry point — typically bound to
    /// <c>CacheInvalidator.InvalidateForDocumentAsync</c> so the document's response /
    /// retrieval cache entries are evicted.
    /// </param>
    /// <param name="logger">Diagnostics logger.</param>
    /// <param name="tenantId">
    /// When set, only events for this tenant are handled (tenant-scoped subscription).
    /// <see langword="null"/> processes every tenant's events.
    /// </param>
    public IngestEventConsumer(
        IChangeFeed feed,
        IDocumentReingestService reingest,
        Func<string, CancellationToken, Task> invalidateCache,
        ILogger<IngestEventConsumer> logger,
        string? tenantId = null)
        : this(feed, reingest, invalidateCache, logger, tenantId, null)
    {
    }

    /// <summary>
    /// Create the consumer with persistent ingestion state store.
    /// </summary>
    public IngestEventConsumer(
        IChangeFeed feed,
        IDocumentReingestService reingest,
        Func<string, CancellationToken, Task> invalidateCache,
        ILogger<IngestEventConsumer> logger,
        string? tenantId,
        IIngestionStateStore? stateStore)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(reingest);
        ArgumentNullException.ThrowIfNull(invalidateCache);
        ArgumentNullException.ThrowIfNull(logger);
        _feed = feed;
        _reingest = reingest;
        _invalidateCache = invalidateCache;
        _logger = logger;
        _tenantId = tenantId;
        _stateStore = stateStore;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var changed in _feed.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            await ProcessAsync(changed, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Process a single event with the tenant / version / idempotency guards. Exposed
    /// so a host (or a test) can drive the consumer one event at a time without the
    /// background loop. Returns <see langword="true"/> if the event was handled
    /// (re-ingest + invalidate ran), <see langword="false"/> if it was skipped (wrong
    /// tenant, stale, or duplicate).
    /// </summary>
    public async Task<bool> ProcessAsync(DocumentChanged changed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changed);

        // Tenant scope: ignore events for other tenants.
        if (_tenantId is not null && !string.Equals(changed.TenantId, _tenantId, StringComparison.Ordinal))
        {
            return false;
        }

        // Version guard (ordering + idempotency): check process cache first, then persistent state if available.
        var last = _lastVersion.GetValueOrDefault(changed.DocumentId, long.MinValue);
        if (_stateStore != null && last == long.MinValue)
        {
            var sourceId = _tenantId ?? "default";
            var persistedState = await _stateStore.GetAsync(changed.DocumentId, sourceId, cancellationToken).ConfigureAwait(false);
            if (persistedState != null && persistedState.Status == IngestionStatus.Succeeded)
            {
                last = persistedState.Version;
                _lastVersion[changed.DocumentId] = last;
            }
        }

        if (changed.Version <= last)
        {
            Log.SkippedStale(_logger, changed.DocumentId, changed.Version, last);
            return false;
        }

        try
        {
            await _reingest.ReingestAsync(changed.DocumentId, cancellationToken).ConfigureAwait(false);
            await _invalidateCache(changed.DocumentId, cancellationToken).ConfigureAwait(false);

            // Record the version only after both side effects succeed, so a failed
            // attempt is retried rather than swallowed by the guard.
            _lastVersion[changed.DocumentId] = changed.Version;
            await _feed.AckAsync(changed, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Leave the event unacked for redelivery / retry; do not advance the guard.
            Log.ReingestFailed(_logger, changed.DocumentId, ex);
            throw;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
            Message = "Skipping stale/duplicate change for {DocumentId}: version {Version} <= processed {Last}.")]
        public static partial void SkippedStale(ILogger logger, string documentId, long version, long last);

        [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Re-ingest failed for {DocumentId}; will retry.")]
        public static partial void ReingestFailed(ILogger logger, string documentId, Exception exception);
    }
}
