using Pigmentor.Evaluation.Metrics;

namespace Pigmentor.Evaluation.Quality;

/// <summary>
/// Evaluates an aggregated evaluation result against specified quality thresholds.
/// </summary>
public static class EvaluationQualityGate
{
    public static (bool Passed, IReadOnlyList<string> Failures) Evaluate(
        RagEvaluationResult result,
        EvaluationThresholds? thresholds = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        thresholds ??= EvaluationThresholds.Default;
        thresholds.Validate();

        if (result.Retrieval.K != thresholds.K)
        {
            throw new ArgumentException(
                $"The evaluated retrieval K ({result.Retrieval.K}) must match the quality threshold K ({thresholds.K}).",
                nameof(thresholds));
        }

        var failures = new List<string>();

        if (result.Retrieval.HitRateAtK < thresholds.MinHitRateAtK)
        {
            failures.Add(FormattableString.Invariant(
                $"HitRate@{thresholds.K} ({result.Retrieval.HitRateAtK:F2}) is below required threshold ({thresholds.MinHitRateAtK:F2})"));
        }

        if (result.Retrieval.RecallAtK < thresholds.MinRecallAtK)
        {
            failures.Add(FormattableString.Invariant(
                $"Recall@{thresholds.K} ({result.Retrieval.RecallAtK:F2}) is below required threshold ({thresholds.MinRecallAtK:F2})"));
        }

        if (result.Retrieval.Mrr < thresholds.MinMrr)
        {
            failures.Add(FormattableString.Invariant(
                $"MRR ({result.Retrieval.Mrr:F2}) is below required threshold ({thresholds.MinMrr:F2})"));
        }

        if (result.Generation.ExpectedFactCoverage < thresholds.MinExpectedFactCoverage)
        {
            failures.Add(FormattableString.Invariant(
                $"Expected Fact Coverage ({result.Generation.ExpectedFactCoverage:F2}) is below required threshold ({thresholds.MinExpectedFactCoverage:F2})"));
        }

        if (result.Generation.TechnicalTermPreservation < thresholds.MinTechnicalTermPreservation)
        {
            failures.Add(FormattableString.Invariant(
                $"Technical Term Preservation ({result.Generation.TechnicalTermPreservation:F2}) is below required threshold ({thresholds.MinTechnicalTermPreservation:F2})"));
        }

        if (result.Generation.FaithfulnessScore < thresholds.MinFaithfulnessScore)
        {
            failures.Add(FormattableString.Invariant(
                $"Faithfulness Score ({result.Generation.FaithfulnessScore:F2}) is below required threshold ({thresholds.MinFaithfulnessScore:F2})"));
        }

        if (result.Generation.InsufficientContextAccuracy < thresholds.MinInsufficientContextAccuracy)
        {
            failures.Add(FormattableString.Invariant(
                $"Insufficient Context Accuracy ({result.Generation.InsufficientContextAccuracy:F2}) is below required threshold ({thresholds.MinInsufficientContextAccuracy:F2})"));
        }

        if (result.Citations.CitationValidity < thresholds.MinCitationValidity)
        {
            failures.Add(FormattableString.Invariant(
                $"Citation Validity ({result.Citations.CitationValidity:F2}) is below required threshold ({thresholds.MinCitationValidity:F2})"));
        }

        if (result.Citations.CitationCoverage < thresholds.MinCitationCoverage)
        {
            failures.Add(FormattableString.Invariant(
                $"Citation Coverage ({result.Citations.CitationCoverage:F2}) is below required threshold ({thresholds.MinCitationCoverage:F2})"));
        }

        return (failures.Count == 0, failures);
    }
}
