namespace Pigmentor.Generation;

/// <summary>
/// Token budget constraints for the RAG turn (prompt + generation).
/// </summary>
/// <param name="MaxTotalTokens">
/// Total maximum token limit allowed for the complete LLM interaction:
/// prompt template instructions + question + context sources + reserved output tokens.
/// Default 2048.
/// </param>
/// <param name="ReservedOutputTokens">
/// Tokens reserved specifically for the LLM output answer.
/// Default 512.
/// </param>
/// <param name="MaxContextTokens">
/// Optional explicit hard cap on context sources alone. If null, context can utilize
/// the remaining budget after output reserve and prompt template overhead.
/// </param>
public readonly record struct ContextBudget(
    int MaxTotalTokens = 2048,
    int ReservedOutputTokens = 512,
    int? MaxContextTokens = null)
{
    /// <summary>
    /// Computes the effective token allowance available for context sources,
    /// accounting for output reserve and prompt overhead (instructions and user question).
    /// </summary>
    /// <param name="promptOverheadTokens">Tokens consumed by prompt instructions and question.</param>
    public int ComputeAvailableContextTokens(int promptOverheadTokens = 0)
    {
        var remaining = Math.Max(0, MaxTotalTokens - ReservedOutputTokens - promptOverheadTokens);
        return MaxContextTokens.HasValue ? Math.Min(MaxContextTokens.Value, remaining) : remaining;
    }

    /// <summary>
    /// Effective tokens available for context sources when prompt overhead is not considered
    /// (MaxTotalTokens - ReservedOutputTokens).
    /// </summary>
    public int AvailableTokens => ComputeAvailableContextTokens(0);
}

