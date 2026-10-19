namespace Pigmentor.Generation;

/// <summary>
/// Token budget constraints for the RAG turn (prompt + generation).
/// </summary>
public readonly record struct ContextBudget
{
    /// <summary>
    /// Total maximum token limit allowed for the complete LLM interaction:
    /// prompt template instructions + question + context sources + reserved output tokens.
    /// Default is 2048.
    /// </summary>
    public int MaxTotalTokens { get; }

    /// <summary>
    /// Tokens reserved specifically for the LLM output answer.
    /// Default is 512.
    /// </summary>
    public int ReservedOutputTokens { get; }

    /// <summary>
    /// Optional explicit hard cap on context sources alone. If null, context can utilize
    /// the remaining budget after output reserve and prompt template overhead.
    /// </summary>
    public int? MaxContextTokens { get; }

    /// <summary>
    /// Total maximum input tokens allowed for the prompt (MaxTotalTokens - ReservedOutputTokens).
    /// </summary>
    public int MaxInputTokens => Math.Max(0, MaxTotalTokens - ReservedOutputTokens);

    public ContextBudget(
        int MaxTotalTokens = 2048,
        int ReservedOutputTokens = 512,
        int? MaxContextTokens = null)
    {
        if (MaxTotalTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTotalTokens), MaxTotalTokens, "MaxTotalTokens must be positive.");
        }
        if (ReservedOutputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ReservedOutputTokens), ReservedOutputTokens, "ReservedOutputTokens cannot be negative.");
        }
        if (ReservedOutputTokens >= MaxTotalTokens)
        {
            throw new ArgumentException("ReservedOutputTokens must be less than MaxTotalTokens.", nameof(ReservedOutputTokens));
        }
        if (MaxContextTokens.HasValue && MaxContextTokens.Value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxContextTokens), MaxContextTokens, "MaxContextTokens cannot be negative.");
        }

        this.MaxTotalTokens = MaxTotalTokens;
        this.ReservedOutputTokens = ReservedOutputTokens;
        this.MaxContextTokens = MaxContextTokens;
    }

    /// <summary>
    /// Computes the effective token allowance available for context sources,
    /// accounting for output reserve and prompt overhead (instructions, question, delimiters, source tags).
    /// Invariant: promptOverheadTokens + ActualContextTokens &lt;= MaxInputTokens.
    /// </summary>
    /// <param name="promptOverheadTokens">Tokens consumed by prompt instructions and question.</param>
    public int ComputeAvailableContextTokens(int promptOverheadTokens = 0)
    {
        var remaining = Math.Max(0, MaxInputTokens - promptOverheadTokens);
        return MaxContextTokens.HasValue ? Math.Min(MaxContextTokens.Value, remaining) : remaining;
    }

    /// <summary>
    /// Effective tokens available for context sources when prompt overhead is not considered
    /// (MaxTotalTokens - ReservedOutputTokens).
    /// </summary>
    public int AvailableTokens => ComputeAvailableContextTokens(0);
}
