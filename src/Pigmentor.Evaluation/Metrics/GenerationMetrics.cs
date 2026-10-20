namespace Pigmentor.Evaluation.Metrics;

/// <summary>
/// Aggregated generation quality metrics across evaluated RAG cases.
/// </summary>
public sealed record GenerationMetrics(
    double ExpectedFactCoverage,
    double TechnicalTermPreservation,
    double FaithfulnessScore,
    double InsufficientContextAccuracy,
    int TotalEvaluated);
