namespace Pigmentor.Evaluation.Quality;

/// <summary>
/// Centralized quality thresholds for RAG regression and evaluation gates.
/// </summary>
public sealed record EvaluationThresholds(
    double MinHitRateAtK = 0.90,
    double MinRecallAtK = 0.85,
    double MinMrr = 0.75,
    double MinExpectedFactCoverage = 0.90,
    double MinTechnicalTermPreservation = 1.00,
    double MinCitationValidity = 1.00,
    double MinCitationCoverage = 0.80,
    int K = 5,
    double MinFaithfulnessScore = 0.90,
    double MinInsufficientContextAccuracy = 1.00)
{
    /// <summary>Default system-wide quality gate policy for Pigmentor v1.</summary>
    public static EvaluationThresholds Default { get; } = new();

    internal void Validate()
    {
        ValidateUnitInterval(MinHitRateAtK, nameof(MinHitRateAtK));
        ValidateUnitInterval(MinRecallAtK, nameof(MinRecallAtK));
        ValidateUnitInterval(MinMrr, nameof(MinMrr));
        ValidateUnitInterval(MinExpectedFactCoverage, nameof(MinExpectedFactCoverage));
        ValidateUnitInterval(MinTechnicalTermPreservation, nameof(MinTechnicalTermPreservation));
        ValidateUnitInterval(MinCitationValidity, nameof(MinCitationValidity));
        ValidateUnitInterval(MinCitationCoverage, nameof(MinCitationCoverage));
        ValidateUnitInterval(MinFaithfulnessScore, nameof(MinFaithfulnessScore));
        ValidateUnitInterval(MinInsufficientContextAccuracy, nameof(MinInsufficientContextAccuracy));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(K);
    }

    private static void ValidateUnitInterval(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0.0 || value > 1.0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Threshold must be finite and between 0 and 1.");
        }
    }
}
