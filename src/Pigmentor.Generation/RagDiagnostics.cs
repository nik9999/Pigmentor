namespace Pigmentor.Generation;

/// <summary>
/// Compact immutable diagnostics for a RAG execution.
/// </summary>
/// <param name="TotalLatency">Total end-to-end execution latency.</param>
/// <param name="RetrievedCandidateCount">Number of candidates returned by retrieval before filtering.</param>
/// <param name="ContextSourceCount">Number of candidates actually included in the generation context.</param>
/// <param name="ContextTokenCount">Estimated token count in the context prompt, or null if uncounted.</param>
/// <param name="FilteredCandidateCount">Number of candidates remaining after metadata filtering.</param>
/// <param name="RerankedCandidateCount">Number of candidates produced after reranking.</param>
/// <param name="PromptTokenCount">Actual counted tokens of the assembled generation prompt.</param>
/// <param name="ValidCitationCount">Number of distinct valid citations mapped to context sources.</param>
/// <param name="InvalidCitationCount">Number of unrecognized citation tags cited by the model.</param>
/// <param name="RetrievalLatency">Latency elapsed during candidate retrieval, if measured.</param>
/// <param name="GenerationLatency">Latency elapsed during chat generation, if measured.</param>
public sealed record RagDiagnostics(
    TimeSpan TotalLatency,
    int RetrievedCandidateCount,
    int ContextSourceCount,
    int? ContextTokenCount = null,
    int FilteredCandidateCount = 0,
    int RerankedCandidateCount = 0,
    int PromptTokenCount = 0,
    int ValidCitationCount = 0,
    int InvalidCitationCount = 0,
    TimeSpan? RetrievalLatency = null,
    TimeSpan? GenerationLatency = null,
    bool RequiredCitationsSatisfied = true,
    bool HasInternalMarkerLeakage = false,
    double? MaxDenseScore = null,
    double? MaxSparseScore = null,
    double? MaxFusedScore = null)
{
    /// <summary>Total execution time in milliseconds.</summary>
    public long TotalLatencyMs => (long)TotalLatency.TotalMilliseconds;

    /// <summary>Total number of citation tags parsed from the answer text.</summary>
    public int CitedTagCount => ValidCitationCount + InvalidCitationCount;

    /// <summary>An empty diagnostics instance with zero counts and latency.</summary>
    public static RagDiagnostics Empty { get; } = new(TimeSpan.Zero, 0, 0);
}
