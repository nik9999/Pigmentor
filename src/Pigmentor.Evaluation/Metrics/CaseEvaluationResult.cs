namespace Pigmentor.Evaluation.Metrics;

/// <summary>
/// Detailed evaluation diagnostics and outcome for a single evaluation case.
/// </summary>
public sealed record CaseEvaluationResult(
    string CaseId,
    string Question,
    bool Passed,
    double HitRate,
    double Recall,
    double Mrr,
    IReadOnlyList<string> RetrievedDocumentIds,
    IReadOnlyList<string> MatchedFacts,
    IReadOnlyList<string> MissingFacts,
    IReadOnlyList<string> MatchedTerms,
    IReadOnlyList<string> MissingTerms,
    int ValidCitations,
    int InvalidCitations,
    double CitationCoverage,
    bool InsufficientContextHandled,
    IReadOnlyList<string> Diagnostics);
