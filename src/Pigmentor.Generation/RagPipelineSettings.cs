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

    private HashSet<string> _excludedEntityProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        "RS-485", "RS-232", "MODBUS-RTU", "MODBUS-TCP", "MODBUS"
    };

    /// <summary>
    /// Protocol names or non-entity technical acronyms to exclude from targeted entity identifier retrieval.
    /// Defaults to RS-485, RS-232, MODBUS-RTU, MODBUS-TCP, and MODBUS.
    /// </summary>
    public HashSet<string> ExcludedEntityProtocols
    {
        get => _excludedEntityProtocols;
        set => _excludedEntityProtocols = value != null
            ? new HashSet<string>(value, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private double _minDenseScore;

    /// <summary>
    /// Minimum dense similarity threshold for candidate admission and abstention calibration.
    /// Default is 0.0 (pass-through / disabled).
    /// When positive (e.g. 0.35), candidates with DenseScore below this threshold are pruned.
    /// </summary>
    public double MinDenseScore
    {
        get => _minDenseScore;
        set => _minDenseScore = value;
    }

    private double _minRerankerScore;

    /// <summary>
    /// Minimum calibrated reranker score threshold for candidate admission and abstention calibration.
    /// Default is 0.0 (pass-through / disabled).
    /// When positive (e.g. 0.35), candidates with RerankerScore below this threshold are pruned.
    /// </summary>
    public double MinRerankerScore
    {
        get => _minRerankerScore;
        set => _minRerankerScore = value;
    }

    private int? _retrievalPoolSize;

    /// <summary>
    /// Initial candidate pool size to retrieve before filtering and reranking.
    /// If null, defaults to the effective topK.
    /// When greater than topK (e.g. 50), the retriever fetches a wide candidate pool for the reranker.
    /// </summary>
    public int? RetrievalPoolSize
    {
        get => _retrievalPoolSize;
        set
        {
            if (value.HasValue && value.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "RetrievalPoolSize must be positive.");
            }
            _retrievalPoolSize = value;
        }
    }

    private bool _enableNearDuplicateSuppression = true;
    private double _nearDuplicateSimilarityCeiling = 0.94;

    /// <summary>
    /// Whether to suppress cross-document near-duplicate chunks prior to context assembly.
    /// Default is true.
    /// </summary>
    public bool EnableNearDuplicateSuppression
    {
        get => _enableNearDuplicateSuppression;
        set => _enableNearDuplicateSuppression = value;
    }

    /// <summary>
    /// Cosine similarity threshold above which chunks from distinct documents are considered near-duplicates.
    /// Must be in (0, 1]. Default is 0.94.
    /// </summary>
    public double NearDuplicateSimilarityCeiling
    {
        get => _nearDuplicateSimilarityCeiling;
        set
        {
            if (value is <= 0.0 or > 1.0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "NearDuplicateSimilarityCeiling must be in (0, 1].");
            }
            _nearDuplicateSimilarityCeiling = value;
        }
    }
}
