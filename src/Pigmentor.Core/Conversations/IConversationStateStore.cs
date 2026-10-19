namespace Pigmentor.Core.Conversations;

/// <summary>
/// The persistence seam for multi-turn conversation state (Ch 25 §"Conversation
/// state persistence"). A RAG agent that holds a session — the running message
/// thread, tool-call state, retrieved-context cursor — needs that state to
/// survive between HTTP requests and across instances behind a load balancer.
/// This port stores an already-serialized session blob keyed by an opaque
/// conversation id, with a time-to-live.
///
/// <para>
/// Two implementations ship: an in-memory store (<c>InMemoryConversationStateStore</c>,
/// the default and the one tests use) and a Redis-backed
/// <c>IDistributedCache</c> store (<c>DistributedConversationStateStore</c> in
/// Pigmentor.Performance — the hot store). Redis is the hot tier; the durable
/// tier is Cosmos DB behind the same seam (the Bicep provisions a Cosmos account
/// for exactly this), swapped in without touching any caller. The contract is
/// deliberately string-in/string-out so the agent layer owns serialization and
/// this seam never takes a dependency on a thread/agent type.
/// </para>
///
/// <para>
/// The TTL is not a cache-tuning knob — it is the <em>retention boundary</em>.
/// It must be derived from the tenant's consent / data-retention policy (Ch 24
/// trust-by-design): a conversation may only be persisted as long as the
/// principal has consented to its retention. Callers pass the consent-derived
/// TTL on every <see cref="SetAsync"/>; an erasure request (Ch 24
/// <c>ErasureReceipt</c>) maps to <see cref="DeleteAsync"/>.
/// </para>
/// </summary>
public interface IConversationStateStore
{
    /// <summary>
    /// Fetch the serialized session for <paramref name="conversationId"/>, or
    /// <see langword="null"/> if it is absent or has expired.
    /// </summary>
    /// <param name="conversationId">The opaque conversation/session id.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<string?> GetAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persist (insert or overwrite) the serialized <paramref name="state"/> for
    /// <paramref name="conversationId"/>, expiring after <paramref name="ttl"/>.
    /// </summary>
    /// <param name="conversationId">The opaque conversation/session id.</param>
    /// <param name="state">The serialized session blob.</param>
    /// <param name="ttl">
    /// The retention window. Derived from the tenant's consent / retention policy
    /// (Ch 24), not a performance tuning value. Must be positive.
    /// </param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SetAsync(string conversationId, string state, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove the session for <paramref name="conversationId"/> if present (the
    /// erasure / end-of-session path). A no-op when the id is unknown.
    /// </summary>
    /// <param name="conversationId">The opaque conversation/session id.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    Task DeleteAsync(string conversationId, CancellationToken cancellationToken = default);
}
