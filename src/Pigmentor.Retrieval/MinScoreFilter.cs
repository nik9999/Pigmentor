using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval;

/// <summary>
/// Score-thresholding decorator — wraps any <see cref="IRetriever"/> and drops
/// inner results whose score falls below <see cref="MinScore"/>.
/// The production safety valve for the "no good match → return nothing" path:
/// when the corpus has nothing relevant, returning an empty list lets generation abstain
/// ("I don't know") instead of being forced to ground an answer in <c>topK</c> irrelevant chunks
/// and hallucinate.
///
/// <para>
/// When evaluating fused hybrid results, if <see cref="RetrievalResult.DenseScore"/> is present,
/// it is evaluated against <see cref="MinScore"/> (enabling cosine-calibrated abstention) rather
/// than the raw RRF rank score, unless a custom <see cref="ScoreSelector"/> is provided.
/// </para>
/// </summary>
public sealed class MinScoreFilter : IRetriever
{
    private readonly IRetriever _inner;

    /// <summary>The minimum score a result must meet to survive the filter.</summary>
    public double MinScore { get; }

    /// <summary>Optional custom score selector for evaluating candidates against MinScore.</summary>
    public Func<RetrievalResult, double>? ScoreSelector { get; }

    /// <summary>
    /// Wrap <paramref name="inner"/> with a minimum-score floor.
    /// </summary>
    /// <param name="inner">The retriever whose results are filtered.</param>
    /// <param name="minScore">
    /// The relevance floor. Results with score &lt; minScore are dropped.
    /// The default <c>0.0</c> disables filtering (pass-through).
    /// </param>
    /// <param name="scoreSelector">
    /// Optional custom score selector. If null, evaluates against <see cref="RetrievalResult.DenseScore"/>
    /// when present, falling back to <see cref="RetrievalResult.Score"/>.
    /// </param>
    public MinScoreFilter(IRetriever inner, double minScore = 0.0, Func<RetrievalResult, double>? scoreSelector = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        MinScore = minScore;
        ScoreSelector = scoreSelector;
    }

    /// <inheritdoc />
    public string Strategy => $"min-score({_inner.Strategy})";

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

        return [.. results.Where(r =>
        {
            var score = ScoreSelector != null ? ScoreSelector(r) : (r.DenseScore ?? r.Score);
            return score >= MinScore;
        })];
    }
}
