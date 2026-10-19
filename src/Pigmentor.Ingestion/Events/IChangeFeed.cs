using System.Threading.Channels;

namespace Pigmentor.Ingestion.Events;

/// <summary>
/// The change-feed transport the <see cref="IngestEventConsumer"/> reads from. In
/// production this is backed by Azure Service Bus / a Kafka topic; the interface
/// is deliberately thin so the consumer never takes a hard dependency on a broker
/// and the whole re-ingest path runs offline in tests over the in-memory
/// <see cref="ChannelChangeFeed"/>.
/// </summary>
public interface IChangeFeed
{
    /// <summary>
    /// Subscribe to the stream of <see cref="DocumentChanged"/> events. The
    /// sequence completes when the feed is closed or <paramref name="cancellationToken"/>
    /// is canceled.
    /// </summary>
    IAsyncEnumerable<DocumentChanged> SubscribeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Acknowledge that an event was processed successfully, so the broker does not
    /// redeliver it. A no-op for at-most-once transports.
    /// </summary>
    Task AckAsync(DocumentChanged changed, CancellationToken cancellationToken = default);
}

/// <summary>
/// An in-process <see cref="IChangeFeed"/> backed by an unbounded
/// <see cref="Channel{T}"/>. Lets the ingest event pipeline run end to end with no
/// broker — used by the unit tests and the dev inner loop. A publisher calls
/// <see cref="PublishAsync"/> (or <see cref="Complete"/> to close the feed); the
/// consumer drains it through <see cref="SubscribeAsync"/>.
/// </summary>
public sealed class ChannelChangeFeed : IChangeFeed
{
    private readonly Channel<DocumentChanged> _channel =
        Channel.CreateUnbounded<DocumentChanged>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>Publish a change event onto the feed.</summary>
    public ValueTask PublishAsync(DocumentChanged changed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changed);
        return _channel.Writer.WriteAsync(changed, cancellationToken);
    }

    /// <summary>Close the feed; the consumer's subscription completes once drained.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    /// <inheritdoc />
    public IAsyncEnumerable<DocumentChanged> SubscribeAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    /// <inheritdoc />
    public Task AckAsync(DocumentChanged changed, CancellationToken cancellationToken = default)
    {
        // In-process delivery is exactly-once already; nothing to ack to a broker.
        ArgumentNullException.ThrowIfNull(changed);
        return Task.CompletedTask;
    }
}
