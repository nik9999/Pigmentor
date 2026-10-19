namespace Pigmentor.Generation;

/// <summary>
/// Configuration settings for <see cref="RagPipeline"/>.
/// </summary>
public sealed record RagPipelineSettings
{
    /// <summary>Default candidate pool size to retrieve. Default is 5.</summary>
    public int DefaultTopK { get; init; } = 5;

    /// <summary>Token budget for context building. Default is 2048 total with 512 reserved output.</summary>
    public ContextBudget Budget { get; init; } = new(MaxTotalTokens: 2048, ReservedOutputTokens: 512);

    /// <summary>Options for assembling and formatting context.</summary>
    public ContextBuilderOptions ContextOptions { get; init; } = new();
}

