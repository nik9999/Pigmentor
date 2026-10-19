using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval;

/// <summary>
/// Score-thresholding decorator — wraps any <see cref="IRetriever"/> and drops
/// inner results whose <see cref="RetrievalResult.Score"/> falls below
/// <see cref="MinScore"/>. The production safety valve for the
/// "no good match → return nothing" path: when the corpus has nothing relevant,
/// returning an empty list lets generation abstain ("I don't know", Ch 10)
/// instead of being forced to ground an answer in <c>topK</c> irrelevant chunks
/// and hallucinate.
///
/// <para>
/// The default floor of <c>0.0</c> is a pass-through (no result is dropped),
/// so wrapping a retriever in <see cref="MinScoreFilter"/> is safe by default;
/// raise the floor to opt in to abstention. May return fewer than <c>topK</c>
/// results — or an empty list — by design.
/// </para>
/// </summary>
public sealed class MinScoreFilter : IRetriever
{
    private readonly IRetriever _inner;

    /// <summary>The minimum score a result must meet to survive the filter.</summary>
    public double MinScore { get; }

    /// <summary>
    /// Wrap <paramref name="inner"/> with a minimum-score floor.
    /// </summary>
    /// <param name="inner">The retriever whose results are filtered.</param>
    /// <param name="minScore">
    /// The relevance floor. Results with <c>Score &lt; minScore</c> are dropped.
    /// The default <c>0.0</c> disables filtering (pass-through).
    /// </param>
    public MinScoreFilter(IRetriever inner, double minScore = 0.0)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        MinScore = minScore;
    }

    /// <inheritdoc />
    public string Strategy => $"min-score({_inner.Strategy})";

    /// <inheritdoc />
    /// <inheritdoc />
    public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default) =>
        RetrieveAsync(new RetrievalRequest(query, topK), cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var results = await _inner.RetrieveAsync(request, cancellationToken).ConfigureAwait(false);

        // A zero (or negative) floor is a pass-through: keep the inner list as-is
        // so the decorator adds no cost when abstention is not configured.
        if (MinScore <= 0.0)
        {
            return results;
        }

        return [.. results.Where(r => r.Score >= MinScore)];
    }
}
