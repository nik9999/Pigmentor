using Pigmentor.Core.Documents;

namespace Pigmentor.Generation;

/// <summary>
/// Result of an <see cref="IRagPipeline"/> query containing the generated answer,
/// citations, retrieved sources, and execution diagnostics.
/// </summary>
/// <param name="Answer">The generated text response.</param>
/// <param name="Citations">Provenance citations linking the answer to source documents and chunks.</param>
/// <param name="Sources">The candidate retrieval results used as context.</param>
/// <param name="Diagnostics">Execution metrics including latency, candidate counts, and token counts.</param>
public sealed record RagResponse(
    string Answer,
    IReadOnlyList<Citation> Citations,
    IReadOnlyList<RetrievalResult> Sources,
    RagDiagnostics Diagnostics)
{
    /// <summary>
    /// Total execution time in milliseconds. Maintained for backwards compatibility.
    /// </summary>
    public long LatencyMs => Diagnostics.TotalLatencyMs;
}

