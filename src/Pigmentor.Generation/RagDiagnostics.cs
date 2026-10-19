namespace Pigmentor.Generation;

/// <summary>
/// Compact immutable diagnostics for a RAG execution.
/// </summary>
/// <param name="TotalLatency">Total end-to-end execution latency.</param>
/// <param name="RetrievedCandidateCount">Number of candidates returned by retrieval before filtering.</param>
/// <param name="ContextSourceCount">Number of candidates actually included in the generation context.</param>
/// <param name="ContextTokenCount">Estimated token count in the context prompt, or null if uncounted.</param>
public sealed record RagDiagnostics(
    TimeSpan TotalLatency,
    int RetrievedCandidateCount,
    int ContextSourceCount,
    int? ContextTokenCount = null)
{
    /// <summary>Total execution time in milliseconds.</summary>
    public long TotalLatencyMs => (long)TotalLatency.TotalMilliseconds;

    /// <summary>An empty diagnostics instance with zero counts and latency.</summary>
    public static RagDiagnostics Empty { get; } = new(TimeSpan.Zero, 0, 0);
}

