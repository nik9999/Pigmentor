using Pigmentor.Core.Documents;

namespace Pigmentor.Generation;

/// <summary>
/// Immutable context package prepared by <see cref="IContextBuilder"/> for LLM prompt augmentation.
/// </summary>
/// <param name="FormattedContext">The assembled context prompt text containing tagged sources.</param>
/// <param name="Sources">The subset of candidates selected and included in the context within budget.</param>
/// <param name="TokenCount">Estimated token count of the formatted context.</param>
/// <param name="SourceMap">Mapping from citation labels (e.g. "S1", "S2") to their source <see cref="RetrievalResult"/>.</param>
public sealed record RagContext(
    string FormattedContext,
    IReadOnlyList<RetrievalResult> Sources,
    int TokenCount,
    IReadOnlyDictionary<string, RetrievalResult> SourceMap)
{
    /// <summary>An empty context package with zero sources and zero tokens.</summary>
    public static RagContext Empty { get; } = new(
        FormattedContext: string.Empty,
        Sources: [],
        TokenCount: 0,
        SourceMap: new Dictionary<string, RetrievalResult>(StringComparer.OrdinalIgnoreCase));
}

