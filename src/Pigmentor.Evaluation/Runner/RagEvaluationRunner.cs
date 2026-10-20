using System.Text.RegularExpressions;
using Pigmentor.Core.Documents;
using Pigmentor.Evaluation.Cases;
using Pigmentor.Evaluation.Metrics;
using Pigmentor.Evaluation.Quality;
using Pigmentor.Generation;

namespace Pigmentor.Evaluation.Runner;

/// <summary>
/// Executes deterministic evaluation across an <see cref="IRagPipeline"/> and a suite of
/// <see cref="RagEvaluationCase"/> instances, gathering retrieval, generation, and citation metrics.
/// </summary>
public static class RagEvaluationRunner
{
    public static async Task<RagEvaluationResult> EvaluateAsync(
        IRagPipeline pipeline,
        IReadOnlyList<RagEvaluationCase> cases,
        int k = 5,
        EvaluationThresholds? thresholds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        if (thresholds is null)
        {
            thresholds = EvaluationThresholds.Default with { K = k };
        }
        else if (thresholds.K != k)
        {
            throw new ArgumentException(
                $"The evaluation K ({k}) must match the quality threshold K ({thresholds.K}).",
                nameof(thresholds));
        }
        thresholds.Validate();
        var caseResults = new List<CaseEvaluationResult>(cases.Count);

        double totalHitRate = 0;
        double totalRecall = 0;
        double totalPrecision = 0;
        double totalMrr = 0;
        double totalNdcg = 0;

        double totalFactCoverage = 0;
        double totalTermPreservation = 0;
        double totalFaithfulness = 0;
        double totalInsufficientContext = 0;
        int evaluatedGenerationCases = 0;
        int insufficientContextCases = 0;

        int grandTotalCitations = 0;
        int grandValidCitations = 0;
        int grandInvalidCitations = 0;
        double totalCitationCoverage = 0;

        foreach (var c in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = new RagRequest(c.Question, metadataFilter: c.MetadataFilter, topK: k);
            var response = await pipeline.AskAsync(request, cancellationToken).ConfigureAwait(false);

            var retrievedDocIds = response.Sources
                .Select(s => s.Chunk.DocumentId)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            // 1. Retrieval Metrics for this case
            double caseHitRate;
            double caseRecall;
            double casePrecision;
            double caseMrr = 0.0;
            double caseNdcg = 0.0;

            var relevantChunkIds = c.RelevantChunkIds is { Count: > 0 }
                ? new HashSet<string>(c.RelevantChunkIds, StringComparer.Ordinal)
                : null;
            var relevantDocumentIds = new HashSet<string>(c.RelevantDocumentIds, StringComparer.Ordinal);
            var expectedRelevantCount = relevantChunkIds?.Count ?? relevantDocumentIds.Count;
            var hitsInTopK = response.Sources.Take(k).ToList();
            bool IsRelevant(RetrievalResult hit) => relevantChunkIds is not null
                ? relevantChunkIds.Contains(hit.Chunk.ChunkId)
                : relevantDocumentIds.Contains(hit.Chunk.DocumentId);

            if (expectedRelevantCount == 0)
            {
                var retrievedNothing = hitsInTopK.Count == 0;
                caseHitRate = retrievedNothing ? 1.0 : 0.0;
                caseRecall = retrievedNothing ? 1.0 : 0.0;
                casePrecision = retrievedNothing ? 1.0 : 0.0;
                caseMrr = retrievedNothing ? 1.0 : 0.0;
                caseNdcg = retrievedNothing ? 1.0 : 0.0;
            }
            else
            {
                var matchedRelevantCount = hitsInTopK
                    .Where(IsRelevant)
                    .Select(h => relevantChunkIds is not null ? h.Chunk.ChunkId : h.Chunk.DocumentId)
                    .Distinct(StringComparer.Ordinal)
                    .Count();

                caseHitRate = matchedRelevantCount > 0 ? 1.0 : 0.0;
                caseRecall = (double)matchedRelevantCount / expectedRelevantCount;
                casePrecision = (double)hitsInTopK.Count(IsRelevant) / k;

                for (var i = 0; i < hitsInTopK.Count; i++)
                {
                    if (IsRelevant(hitsInTopK[i]))
                    {
                        caseMrr = 1.0 / (i + 1);
                        break;
                    }
                }

                double dcg = 0.0;
                for (var i = 0; i < hitsInTopK.Count; i++)
                {
                    if (IsRelevant(hitsInTopK[i]))
                    {
                        dcg += 1.0 / Math.Log2(i + 2);
                    }
                }

                double idcg = 0.0;
                for (var i = 0; i < Math.Min(k, expectedRelevantCount); i++)
                {
                    idcg += 1.0 / Math.Log2(i + 2);
                }
                caseNdcg = idcg == 0.0 ? 0.0 : dcg / idcg;
            }

            totalHitRate += caseHitRate;
            totalRecall += caseRecall;
            totalPrecision += casePrecision;
            totalMrr += caseMrr;
            totalNdcg += caseNdcg;

            // 2. Generation & Grounding for this case
            var matchedFacts = new List<string>();
            var missingFacts = new List<string>();
            var matchedTerms = new List<string>();
            var missingTerms = new List<string>();
            var diagnostics = new List<string>();

            if (c.ExpectedFacts is { Count: > 0 })
            {
                foreach (var fact in c.ExpectedFacts)
                {
                    if (ContainsNormalized(response.Answer, fact))
                    {
                        matchedFacts.Add(fact);
                    }
                    else
                    {
                        missingFacts.Add(fact);
                    }
                }
            }

            if (c.ExpectedTechnicalTerms is { Count: > 0 })
            {
                foreach (var term in c.ExpectedTechnicalTerms)
                {
                    // Strict exact spelling and casing for technical terms (§13)
                    if (response.Answer.Contains(term, StringComparison.Ordinal))
                    {
                        matchedTerms.Add(term);
                    }
                    else
                    {
                        missingTerms.Add(term);
                    }
                }
            }

            double factCoverage = c.ExpectedFacts is { Count: > 0 }
                ? (double)matchedFacts.Count / c.ExpectedFacts.Count
                : 1.0;

            double termPreservation = c.ExpectedTechnicalTerms is { Count: > 0 }
                ? (double)matchedTerms.Count / c.ExpectedTechnicalTerms.Count
                : 1.0;

            bool insufficientContextHandled = true;
            if (c.ExpectInsufficientContext)
            {
                insufficientContextCases++;
                insufficientContextHandled = SignalsInsufficientContext(response.Answer);
                totalInsufficientContext += insufficientContextHandled ? 1.0 : 0.0;

                if (!insufficientContextHandled)
                {
                    diagnostics.Add("Failed to signal insufficient context for unanswerable query.");
                }
            }

            evaluatedGenerationCases++;
            totalFactCoverage += factCoverage;
            totalTermPreservation += termPreservation;

            // 3. Citation Validity and Coverage for this case
            // Valid citation: references a source that actually exists in response.Sources
            var availableSources = response.Sources
                .Select((source, idx) => (Tag: FormattableString.Invariant($"S{idx + 1}"), Source: source, Index: idx + 1))
                .ToDictionary(x => x.Tag, StringComparer.OrdinalIgnoreCase);
            var availableSourceTags = availableSources.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var citations = response.Citations;
            var validCitationsCount = 0;
            var invalidCitationsCount = response.Diagnostics.InvalidCitationCount;

            if (citations.Count > 0)
            {
                foreach (var citation in citations)
                {
                    if (availableSources.TryGetValue(citation.Label, out var mapped)
                        && citation.SourceIndex == mapped.Index
                        && string.Equals(citation.DocumentId, mapped.Source.Chunk.DocumentId, StringComparison.Ordinal)
                        && string.Equals(citation.ChunkId, mapped.Source.Chunk.ChunkId, StringComparison.Ordinal))
                    {
                        validCitationsCount++;
                    }
                    else
                    {
                        invalidCitationsCount++;
                    }
                }
            }

            // Also inspect answer text for citations to ensure hallucinated tags like [S99] are caught even in custom wrappers
            var textMatches = Regex.Matches(response.Answer ?? string.Empty, @"\[(S\d+)\]", RegexOptions.IgnoreCase);
            var seenTagsInText = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var textValid = 0;
            var textInvalid = 0;
            foreach (Match match in textMatches)
            {
                var tag = match.Groups[1].Value.ToUpperInvariant();
                if (seenTagsInText.Add(tag))
                {
                    if (availableSourceTags.Contains(tag))
                    {
                        textValid++;
                    }
                    else
                    {
                        textInvalid++;
                    }
                }
            }

            if (citations.Count == 0 && textValid > 0)
            {
                validCitationsCount = textValid;
            }
            invalidCitationsCount = Math.Max(invalidCitationsCount, textInvalid);

            var totalCaseCitations = validCitationsCount + invalidCitationsCount;
            grandTotalCitations += totalCaseCitations;
            grandValidCitations += validCitationsCount;
            grandInvalidCitations += invalidCitationsCount;

            double caseCitationCoverage;
            if (c.ExpectInsufficientContext)
            {
                caseCitationCoverage = insufficientContextHandled ? 1.0 : 0.0;
            }
            else if (c.ExpectedFacts is { Count: > 0 })
            {
                caseCitationCoverage = validCitationsCount > 0 ? factCoverage : 0.0;
            }
            else
            {
                caseCitationCoverage = 1.0;
            }

            totalCitationCoverage += caseCitationCoverage;

            // Deterministic grounding: expected claims must occur in both the answer and retrieved context.
            double faithfulness;
            if (c.ExpectInsufficientContext)
            {
                faithfulness = insufficientContextHandled ? 1.0 : 0.0;
            }
            else if (string.IsNullOrWhiteSpace(response.Answer))
            {
                faithfulness = 0.0;
            }
            else
            {
                var expectedClaims = (c.ExpectedFacts ?? [])
                    .Concat(c.ExpectedTechnicalTerms ?? [])
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var contextText = string.Join("\n", response.Sources.Select(s => s.Chunk.Text));
                var groundingChecks = expectedClaims
                    .Where(claim => ContainsNormalized(response.Answer, claim))
                    .Concat(ExtractTechnicalEvidence(response.Answer))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                faithfulness = groundingChecks.Count == 0
                    ? (response.Sources.Count > 0 ? 1.0 : 0.0)
                    : (double)groundingChecks.Count(claim => ContainsNormalized(contextText, claim)) / groundingChecks.Count;

                if (invalidCitationsCount > 0)
                {
                    faithfulness = Math.Min(faithfulness, Math.Max(0.0, 1.0 - (0.3 * invalidCitationsCount)));
                }
            }
            totalFaithfulness += faithfulness;

            // Diagnostics and pass/fail verdict
            if (caseHitRate < 1.0)
            {
                var expectedIds = relevantChunkIds is not null ? c.RelevantChunkIds! : c.RelevantDocumentIds;
                diagnostics.Add(expectedRelevantCount == 0
                    ? "Retrieval returned sources for a query expected to have no relevant context."
                    : FormattableString.Invariant(
                        $"Retrieval missed expected {(relevantChunkIds is not null ? "chunks" : "documents")} [{string.Join(", ", expectedIds)}]. Got [{string.Join(", ", retrievedDocIds)}]."));
            }

            if (missingFacts.Count > 0)
            {
                diagnostics.Add(FormattableString.Invariant($"Missing expected facts: [{string.Join(", ", missingFacts)}]."));
            }

            if (missingTerms.Count > 0)
            {
                diagnostics.Add(FormattableString.Invariant($"Missing exact technical terms: [{string.Join(", ", missingTerms)}]."));
            }

            if (invalidCitationsCount > 0)
            {
                diagnostics.Add(FormattableString.Invariant($"Encountered {invalidCitationsCount} invalid or hallucinated citation(s)."));
            }

            if (faithfulness < 1.0)
            {
                diagnostics.Add(FormattableString.Invariant($"Answer faithfulness score was {faithfulness:F2}."));
            }

            var passed = caseHitRate >= 1.0
                && missingFacts.Count == 0
                && missingTerms.Count == 0
                && invalidCitationsCount == 0
                && insufficientContextHandled
                && faithfulness >= 1.0;

            caseResults.Add(new CaseEvaluationResult(
                CaseId: c.Id,
                Question: c.Question,
                Passed: passed,
                HitRate: caseHitRate,
                Recall: caseRecall,
                Mrr: caseMrr,
                RetrievedDocumentIds: retrievedDocIds,
                MatchedFacts: matchedFacts,
                MissingFacts: missingFacts,
                MatchedTerms: matchedTerms,
                MissingTerms: missingTerms,
                ValidCitations: validCitationsCount,
                InvalidCitations: invalidCitationsCount,
                CitationCoverage: caseCitationCoverage,
                InsufficientContextHandled: insufficientContextHandled,
                Diagnostics: diagnostics));
        }

        var totalCount = cases.Count;
        var passedCount = caseResults.Count(r => r.Passed);
        var failedCount = totalCount - passedCount;

        var retMetrics = new RetrievalMetrics(
            RecallAtK: totalCount > 0 ? totalRecall / totalCount : 0.0,
            PrecisionAtK: totalCount > 0 ? totalPrecision / totalCount : 0.0,
            Mrr: totalCount > 0 ? totalMrr / totalCount : 0.0,
            NdcgAtK: totalCount > 0 ? totalNdcg / totalCount : 0.0,
            K: k,
            QueryCount: totalCount,
            HitRateAtK: totalCount > 0 ? totalHitRate / totalCount : 0.0);

        var genMetrics = new GenerationMetrics(
            ExpectedFactCoverage: evaluatedGenerationCases > 0 ? totalFactCoverage / evaluatedGenerationCases : 0.0,
            TechnicalTermPreservation: evaluatedGenerationCases > 0 ? totalTermPreservation / evaluatedGenerationCases : 0.0,
            FaithfulnessScore: evaluatedGenerationCases > 0 ? totalFaithfulness / evaluatedGenerationCases : 0.0,
            InsufficientContextAccuracy: insufficientContextCases > 0 ? totalInsufficientContext / insufficientContextCases : 1.0,
            TotalEvaluated: evaluatedGenerationCases);

        double citationValidity = grandTotalCitations > 0
            ? (double)grandValidCitations / grandTotalCitations
            : 1.0;

        double citationCoverage = totalCount > 0
            ? totalCitationCoverage / totalCount
            : 1.0;

        var citMetrics = new CitationMetrics(
            CitationValidity: citationValidity,
            CitationCoverage: citationCoverage,
            TotalCitations: grandTotalCitations,
            ValidCitations: grandValidCitations,
            InvalidCitations: grandInvalidCitations);

        var preResult = new RagEvaluationResult(
            TotalCases: totalCount,
            PassedCases: passedCount,
            FailedCases: failedCount,
            Retrieval: retMetrics,
            Generation: genMetrics,
            Citations: citMetrics,
            CaseResults: caseResults,
            QualityGatePassed: false,
            QualityGateFailures: null);

        var (gatePassed, gateFailures) = EvaluationQualityGate.Evaluate(preResult, thresholds);

        return preResult with
        {
            QualityGatePassed = gatePassed,
            QualityGateFailures = gateFailures
        };
    }

    private static bool ContainsNormalized(string text, string target)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        var normText = NormalizeWhitespace(text).ToLowerInvariant();
        var normTarget = NormalizeWhitespace(target).ToLowerInvariant();

        if (normText.Contains(normTarget, StringComparison.Ordinal))
        {
            return true;
        }

        // Conservative prefix matching for inflected words; numeric and technical tokens remain exact.
        var textWords = Tokenize(normText);
        var targetWords = Tokenize(normTarget);
        if (targetWords.Length > 0)
        {
            return targetWords.All(targetWord => textWords.Any(textWord => TokensMatch(textWord, targetWord)));
        }

        return false;
    }

    private static string NormalizeWhitespace(string input) =>
        Regex.Replace(input, @"\s+", " ").Trim();

    private static string[] Tokenize(string input) =>
        Regex.Matches(input, @"[\p{L}\p{N}]+(?:[-./][\p{L}\p{N}]+)*")
            .Select(match => match.Value)
            .ToArray();

    private static bool TokensMatch(string textWord, string targetWord)
    {
        if (textWord.Equals(targetWord, StringComparison.Ordinal))
        {
            return true;
        }

        if (targetWord.Length <= 4 || targetWord.Any(char.IsDigit) || textWord.Any(char.IsDigit))
        {
            return false;
        }

        return textWord.StartsWith(targetWord[..4], StringComparison.Ordinal);
    }

    private static bool SignalsInsufficientContext(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        var lowerAnswer = answer.ToLowerInvariant();
        return lowerAnswer.Contains("недостаточно", StringComparison.Ordinal)
            || lowerAnswer.Contains("не найден", StringComparison.Ordinal)
            || lowerAnswer.Contains("не указан", StringComparison.Ordinal)
            || lowerAnswer.Contains("нет информации", StringComparison.Ordinal)
            || lowerAnswer.Contains("cannot answer", StringComparison.Ordinal)
            || lowerAnswer.Contains("insufficient", StringComparison.Ordinal);
    }

    private static IEnumerable<string> ExtractTechnicalEvidence(string answer)
    {
        var answerWithoutCitations = Regex.Replace(answer, @"\[S\d+\]", string.Empty, RegexOptions.IgnoreCase);
        return Regex.Matches(answerWithoutCitations, @"[\p{L}\p{N}]+(?:[-./][\p{L}\p{N}]+)*")
            .Select(match => match.Value)
            .Where(token => token.Any(char.IsDigit)
                || token.Contains('-', StringComparison.Ordinal)
                || (token.Length >= 2 && token.All(c => !char.IsLetter(c) || char.IsUpper(c))));
    }
}
