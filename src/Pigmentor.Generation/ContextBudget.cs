namespace Pigmentor.Generation;

/// <summary>
/// Token budget constraints for context building, reserving capacity for generation output.
/// </summary>
/// <param name="MaxTotalTokens">Total maximum tokens allowed for context and generation. Default 2048.</param>
/// <param name="ReservedOutputTokens">Tokens reserved for the LLM output answer. Default 512.</param>
public readonly record struct ContextBudget(int MaxTotalTokens = 2048, int ReservedOutputTokens = 512)
{
    /// <summary>
    /// Effective tokens available for the context section.
    /// </summary>
    public int AvailableTokens => Math.Max(0, MaxTotalTokens - ReservedOutputTokens);
}

