namespace Pigmentor.Core.Configuration;

/// <summary>
/// Reranking knobs bound from the <c>Pigmentor:Reranking</c> section of
/// <c>appsettings.json</c>. Controls whether — and how — the retriever is
/// wrapped in the <em>retrieve-broadly, rerank-precisely</em> middleware
/// (Chapter 9).
/// </summary>
public sealed class RerankingOptions
{
    /// <summary>
    /// Which reranker to use. One of <c>none</c>, <c>llm</c>, <c>cohere</c>, or
    /// <c>onnx</c>. <c>none</c> selects the pass-through <c>NoOpReranker</c> — the
    /// dev-time default, the control arm of A/B tests, and the safe fallback when
    /// a reranker is unavailable. <c>llm</c> uses the LLM-as-judge reranker,
    /// <c>cohere</c> the hosted Cohere Rerank API, and <c>onnx</c> the self-hosted
    /// ONNX cross-encoder (requires the opt-in <c>Pigmentor.Reranking.Onnx</c>
    /// adapter to register an <c>ICrossEncoderModel</c>).
    /// </summary>
    public string Mode { get; set; } = "none";

    /// <summary>
    /// How many candidates to pull from the inner retriever before reranking
    /// down to the caller's <c>topK</c>. Wider pools give the reranker more to
    /// work with at the cost of latency; the repo default is 20.
    /// </summary>
    public int CandidateCount { get; set; } = 20;

    /// <summary>
    /// Optional relevance floor. Results scoring below it are dropped after
    /// reranking, which may yield fewer than <c>topK</c> results or an empty
    /// list so the generator can abstain. <c>0.0</c> (the default) disables the
    /// floor.
    /// </summary>
    public double MinScore { get; set; }
}
