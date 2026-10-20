namespace Pigmentor.Core.Abstractions;

/// <summary>
/// Evaluation result of the OCR / text quality gate on extracted or recognized text.
/// </summary>
public sealed record TextQualityAssessment(
    double Score,
    bool IsAcceptable,
    IReadOnlyList<string> Reasons)
{
    /// <summary>Acceptable assessment with a perfect score.</summary>
    public static TextQualityAssessment Acceptable(double score = 1.0) =>
        new(score, true, []);

    /// <summary>Rejected assessment with specific failure reasons.</summary>
    public static TextQualityAssessment Rejected(double score, params string[] reasons) =>
        new(score, false, reasons);
}

