using Pigmentor.Core.Abstractions;

namespace Pigmentor.Retrieval;

/// <summary>
/// Selects a retrieval strategy by name — <c>"dense"</c>, <c>"sparse"</c>, or
/// <c>"hybrid"</c> — from the dense and sparse legs the caller already built.
/// This is the small, tested factory behind the chapter's "pick dense / sparse /
/// hybrid by config" idea: rather than registering one fixed <see cref="IRetriever"/>,
/// resolve the mode from configuration at composition time.
///
/// <para>
/// Standalone on purpose — it does not touch the existing DI registration in a
/// breaking way and adds no <c>PigmentorOptions.Retrieval.Mode</c> that could
/// collide with the rest of the configuration surface. Wire it into DI with a
/// one-liner: <c>services.AddSingleton&lt;IRetriever&gt;(sp =&gt;
/// RetrieverModeFactory.Create(mode, sp.GetRequiredService&lt;DenseRetriever&gt;(), …));</c>
/// </para>
/// </summary>
public static class RetrieverModeFactory
{
    /// <summary>The retrieval modes this factory understands.</summary>
    public static readonly IReadOnlyList<string> SupportedModes = ["dense", "sparse", "hybrid"];

    /// <summary>
    /// Build the retriever for <paramref name="mode"/>.
    /// </summary>
    /// <param name="mode">
    /// One of <c>"dense"</c>, <c>"sparse"</c>, or <c>"hybrid"</c>
    /// (case-insensitive, surrounding whitespace ignored).
    /// </param>
    /// <param name="dense">The dense retrieval leg (used by <c>dense</c> and <c>hybrid</c>).</param>
    /// <param name="sparse">The sparse retrieval leg (used by <c>sparse</c> and <c>hybrid</c>).</param>
    /// <param name="merger">
    /// Optional RRF merger for the <c>hybrid</c> mode. Ignored by <c>dense</c> and
    /// <c>sparse</c>; defaults to a fresh <see cref="RrfMerger"/> when omitted.
    /// </param>
    /// <returns>The selected <see cref="IRetriever"/>.</returns>
    /// <exception cref="ArgumentException">If <paramref name="mode"/> is not a supported mode.</exception>
    public static IRetriever Create(
        string mode,
        IRetriever dense,
        IRetriever sparse,
        RrfMerger? merger = null,
        HybridRetrievalOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentNullException.ThrowIfNull(dense);
        ArgumentNullException.ThrowIfNull(sparse);

        return mode.Trim().ToLowerInvariant() switch
        {
            "dense" => dense,
            "sparse" => sparse,
            "hybrid" => new HybridRetriever(dense, sparse, options, merger),
            _ => throw new ArgumentException(
                $"Unknown retriever mode '{mode}'. Supported modes: {string.Join(", ", SupportedModes)}.",
                nameof(mode)),
        };
    }
}
