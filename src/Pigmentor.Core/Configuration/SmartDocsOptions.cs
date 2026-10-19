namespace Pigmentor.Core.Configuration;

/// <summary>
/// Umbrella options object bound to the top-level <c>Pigmentor</c> configuration
/// section. Groups the cross-cutting Pigmentor settings so a single
/// <c>IOptions&lt;PigmentorOptions&gt;</c> resolve exposes every sub-section.
/// </summary>
public sealed class PigmentorOptions
{
    /// <summary>Configuration section path bound to this class.</summary>
    public const string SectionName = "Pigmentor";

    /// <summary>Ingestion-time chunking settings (Chapter 4).</summary>
    public IngestionOptions Ingestion { get; set; } = new();

    /// <summary>Reranking settings — mode, candidate pool, and relevance floor (Chapter 9).</summary>
    public RerankingOptions Reranking { get; set; } = new();
}
