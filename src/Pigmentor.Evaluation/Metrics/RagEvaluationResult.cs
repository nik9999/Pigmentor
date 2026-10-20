using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Pigmentor.Evaluation.Metrics;

/// <summary>
/// Aggregated machine-readable evaluation result across the full RAG pipeline evaluation suite.
/// </summary>
public sealed record RagEvaluationResult(
    int TotalCases,
    int PassedCases,
    int FailedCases,
    RetrievalMetrics Retrieval,
    GenerationMetrics Generation,
    CitationMetrics Citations,
    IReadOnlyList<CaseEvaluationResult> CaseResults,
    bool QualityGatePassed,
    IReadOnlyList<string>? QualityGateFailures = null)
{
    /// <summary>Fraction of evaluation cases that passed all individual verification checks.</summary>
    public double SuccessRate => TotalCases > 0 ? (double)PassedCases / TotalCases : 0.0;

    /// <summary>
    /// Renders a human-readable structured summary report.
    /// </summary>
    public string ToSummaryString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Pigmentor RAG Evaluation Report ===");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Cases Total: {TotalCases} | Passed: {PassedCases} | Failed: {FailedCases} | Success Rate: {SuccessRate:P1}");
        sb.AppendLine();
        sb.AppendLine("--- Retrieval Quality ---");
        sb.AppendLine(CultureInfo.InvariantCulture, $"HitRate@{Retrieval.K}: {Retrieval.HitRateAtK:F2}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Recall@{Retrieval.K}:  {Retrieval.RecallAtK:F2}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Precision@{Retrieval.K}: {Retrieval.PrecisionAtK:F2}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"MRR:        {Retrieval.Mrr:F2}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"nDCG@{Retrieval.K}: {Retrieval.NdcgAtK:F2}");
        sb.AppendLine();
        sb.AppendLine("--- Generation & Grounding ---");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Expected Fact Coverage:       {Generation.ExpectedFactCoverage:F2}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Technical Term Preservation: {Generation.TechnicalTermPreservation:F2}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Faithfulness / Groundedness: {Generation.FaithfulnessScore:F2}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Insufficient Context Acc:    {Generation.InsufficientContextAccuracy:F2}");
        sb.AppendLine();
        sb.AppendLine("--- Citations ---");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Citation Validity: {Citations.CitationValidity:F2} ({Citations.ValidCitations}/{Citations.TotalCitations} valid, {Citations.InvalidCitations} invalid)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Citation Coverage: {Citations.CitationCoverage:F2}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Quality Gate: {(QualityGatePassed ? "PASS" : "FAIL")}");

        if (QualityGateFailures is { Count: > 0 })
        {
            sb.AppendLine("Failures:");
            foreach (var failure in QualityGateFailures)
            {
                sb.AppendLine(FormattableString.Invariant($"  - {failure}"));
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Serializes the evaluation result into a standardized JSON string.
    /// </summary>
    public string ToJson(bool writeIndented = true)
    {
        return JsonSerializer.Serialize(this, writeIndented ? IndentedJsonOptions : CompactJsonOptions);
    }
}
