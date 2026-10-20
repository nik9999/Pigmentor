namespace Pigmentor.Core.Abstractions;

/// <summary>
/// Defensive resource constraints and safety thresholds for document extraction and processing.
/// </summary>
public sealed record ExtractionLimits
{
    /// <summary>Maximum allowed file size in bytes (default: 50 MB).</summary>
    public long MaxFileSizeBytes { get; init; } = 50 * 1024 * 1024;

    /// <summary>Maximum number of pages extracted from paginated documents (default: 1000).</summary>
    public int MaxPages { get; init; } = 1000;

    /// <summary>Maximum cumulative extracted text length in characters (default: 5,000,000).</summary>
    public int MaxTextLength { get; init; } = 5_000_000;

    /// <summary>Maximum number of rows extracted per table (default: 10,000).</summary>
    public int MaxTableRows { get; init; } = 10_000;

    /// <summary>Default system-wide limits.</summary>
    public static ExtractionLimits Default { get; } = new();
}

