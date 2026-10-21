namespace Pigmentor.Generation;

/// <summary>
/// Configuration settings for <see cref="RagPipeline"/>.
/// </summary>
public sealed record RagPipelineSettings
{
    private int _defaultTopK = 5;
    private ContextBudget _budget = new(MaxTotalTokens: 8192, ReservedOutputTokens: 1024);
    private ContextBuilderOptions _contextOptions = new();

    /// <summary>Default candidate pool size to retrieve. Must be positive. Default is 5.</summary>
    public int DefaultTopK
    {
        get => _defaultTopK;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "DefaultTopK must be positive.");
            }
            _defaultTopK = value;
        }
    }

    /// <summary>Token budget for context building. Default is 8192 total with 1024 reserved output.</summary>
    public ContextBudget Budget
    {
        get => _budget;
        set => _budget = value;
    }

    /// <summary>Options for assembling and formatting context.</summary>
    public ContextBuilderOptions ContextOptions
    {
        get => _contextOptions;
        set => _contextOptions = value ?? throw new ArgumentNullException(nameof(value));
    }
}
