namespace Pigmentor.Generation;

/// <summary>
/// Behavior when an individual chunk's token size exceeds the available token budget.
/// </summary>
public enum OversizedChunkBehavior
{
    /// <summary>Truncate the chunk text so that it fits within the remaining token budget.</summary>
    Truncate,

    /// <summary>Drop/skip the chunk entirely if it exceeds the remaining token budget.</summary>
    Drop
}

/// <summary>
/// Options for configuring context assembly and formatting.
/// </summary>
public sealed record ContextBuilderOptions
{
    /// <summary>Default context budget.</summary>
    public ContextBudget Budget { get; init; } = new(MaxTotalTokens: 2048, ReservedOutputTokens: 512);

    /// <summary>Whether to apply lost-in-the-middle interleaving via <see cref="Pigmentor.Retrieval.ContextOrdering"/>.</summary>
    public bool EnableInterleaving { get; init; } = true;

    /// <summary>Strategy for handling chunks that exceed the token budget.</summary>
    public OversizedChunkBehavior OversizedBehavior { get; init; } = OversizedChunkBehavior.Truncate;
}

