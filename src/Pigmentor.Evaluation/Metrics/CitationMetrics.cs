namespace Pigmentor.Evaluation.Metrics;

/// <summary>
/// Aggregated citation quality and validity metrics across evaluated RAG cases.
/// </summary>
public sealed record CitationMetrics(
    double CitationValidity,
    double CitationCoverage,
    int TotalCitations,
    int ValidCitations,
    int InvalidCitations);
