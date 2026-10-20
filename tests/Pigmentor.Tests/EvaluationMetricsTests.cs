using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;
using Pigmentor.Evaluation;
using Pigmentor.Evaluation.Cases;
using Pigmentor.Evaluation.Metrics;
using Pigmentor.Evaluation.Quality;
using Pigmentor.Evaluation.Runner;
using Pigmentor.Generation;
using Pigmentor.Retrieval;
using Xunit;

namespace Pigmentor.Tests;

public sealed class EvaluationMetricsTests
{
    // --- 1. RetrievalEvaluator Unit Tests ---

    [Fact]
    public void RetrievalEvaluator_HitRate_CalculatesExpectedValues()
    {
        var goldA = new GoldenItem("q1", new HashSet<string> { "doc-a" });
        var goldB = new GoldenItem("q2", new HashSet<string> { "doc-b" });
        var goldC = new GoldenItem("q3", new HashSet<string> { "doc-c" });

        // All 3 queries hit at rank 1
        var perfectSamples = new List<(GoldenItem Gold, IReadOnlyList<RetrievalResult> Hits)>
        {
            (goldA, new[] { CreateHit("doc-a"), CreateHit("doc-x") }),
            (goldB, new[] { CreateHit("doc-b"), CreateHit("doc-y") }),
            (goldC, new[] { CreateHit("doc-c"), CreateHit("doc-z") })
        };
        var res1 = RetrievalEvaluator.Evaluate(perfectSamples, 2);
        Assert.Equal(1.0, res1.HitRateAtK);
        Assert.Equal(1.0, res1.RecallAtK);
        Assert.Equal(1.0, res1.Mrr);

        // 1 query hits at rank 2, 1 at rank 1, 1 misses
        var mixedSamples = new List<(GoldenItem Gold, IReadOnlyList<RetrievalResult> Hits)>
        {
            (goldA, new[] { CreateHit("doc-x"), CreateHit("doc-a") }), // rank 2 hit
            (goldB, new[] { CreateHit("doc-b"), CreateHit("doc-y") }), // rank 1 hit
            (goldC, new[] { CreateHit("doc-x"), CreateHit("doc-y") })  // miss
        };
        var res2 = RetrievalEvaluator.Evaluate(mixedSamples, 2);
        Assert.Equal(2.0 / 3.0, res2.HitRateAtK, precision: 3);
        // MRR = (1/2 + 1/1 + 0) / 3 = 1.5 / 3 = 0.5
        Assert.Equal(0.5, res2.Mrr, precision: 3);

        // All miss
        var zeroSamples = new List<(GoldenItem Gold, IReadOnlyList<RetrievalResult> Hits)>
        {
            (goldA, new[] { CreateHit("other-1") }),
            (goldB, new[] { CreateHit("other-2") }),
            (goldC, new[] { CreateHit("other-3") })
        };
        var res3 = RetrievalEvaluator.Evaluate(zeroSamples, 1);
        Assert.Equal(0.0, res3.HitRateAtK);
        Assert.Equal(0.0, res3.RecallAtK);
        Assert.Equal(0.0, res3.Mrr);
    }

    [Fact]
    public void RetrievalEvaluator_RagEvaluationCasesOverload_WorksEquivalently()
    {
        var case1 = new RagEvaluationCase("c1", "Q1?", RelevantDocumentIds: ["doc-1"]);
        var case2 = new RagEvaluationCase("c2", "Q2?", RelevantDocumentIds: ["doc-2"]);

        var samples = new List<(RagEvaluationCase Case, IReadOnlyList<RetrievalResult> Hits)>
        {
            (case1, new[] { CreateHit("doc-1"), CreateHit("noise") }),
            (case2, new[] { CreateHit("noise"), CreateHit("doc-2") })
        };

        var metrics = RetrievalEvaluator.Evaluate(samples, 2);
        Assert.Equal(1.0, metrics.HitRateAtK);
        Assert.Equal(1.0, metrics.RecallAtK);
        Assert.Equal(0.75, metrics.Mrr, precision: 2); // (1.0 + 0.5) / 2 = 0.75
    }

    [Fact]
    public void RetrievalEvaluator_RagEvaluationCasesOverload_UsesRelevantChunkIds()
    {
        var c = new RagEvaluationCase(
            "chunk-specific",
            "Q?",
            RelevantDocumentIds: ["doc-1"],
            RelevantChunkIds: ["doc-1#7"]);
        var samples = new List<(RagEvaluationCase Case, IReadOnlyList<RetrievalResult> Hits)>
        {
            (c, new[] { new RetrievalResult(CreateChunk("doc-1", "wrong section", "doc-1#0"), 1.0) })
        };

        var metrics = RetrievalEvaluator.Evaluate(samples, 1);

        Assert.Equal(0.0, metrics.HitRateAtK);
        Assert.Equal(0.0, metrics.RecallAtK);
    }

    // --- 2. Generation & Citation Metrics Unit Tests via RagEvaluationRunner ---

    [Fact]
    public async Task RagEvaluationRunner_StrictTechnicalTermMatching_FailsOnCaseOrSpellingMismatch()
    {
        // Expecting strict "RS-485", answer provides lowercase "rs-485"
        var c = new RagEvaluationCase(
            Id: "case-term-strict",
            Question: "Какой протокол?",
            RelevantDocumentIds: ["doc-comm"],
            ExpectedFacts: ["скорость 115200"],
            ExpectedTechnicalTerms: ["RS-485"]);

        var fakeChunk = CreateChunk("doc-comm", "RS-485 speed 115200");
        var fakePipeline = new FakePipeline(
            answer: "Используется протокол rs-485, скорость 115200 [S1].",
            sources: [new RetrievalResult(fakeChunk, 0.99)]);

        var result = await RagEvaluationRunner.EvaluateAsync(fakePipeline, [c], k: 5);

        // Fact matched (case-insensitive)
        Assert.Equal(1.0, result.Generation.ExpectedFactCoverage);
        // Term failed: "rs-485" != "RS-485" (strict case-sensitive invariant §13)
        Assert.Equal(0.0, result.Generation.TechnicalTermPreservation);
        Assert.False(result.QualityGatePassed);
        Assert.Contains(result.QualityGateFailures!, f => f.Contains("Technical Term Preservation"));
    }

    [Fact]
    public async Task RagEvaluationRunner_FactCoverage_CalculatesAccurately()
    {
        var c = new RagEvaluationCase(
            Id: "case-facts",
            Question: "Параметры?",
            RelevantDocumentIds: ["doc-1"],
            ExpectedFacts: ["Fact Alpha", "Fact Beta", "Fact Gamma"],
            ExpectedTechnicalTerms: []);

        var fakeChunk = CreateChunk("doc-1", "Alpha and Beta facts present");
        var fakePipeline = new FakePipeline(
            answer: "Мы подтверждаем fact alpha и fact beta [S1].", // Missing Gamma
            sources: [new RetrievalResult(fakeChunk, 0.95)]);

        var result = await RagEvaluationRunner.EvaluateAsync(fakePipeline, [c], k: 5);

        Assert.Equal(2.0 / 3.0, result.Generation.ExpectedFactCoverage, precision: 2);
        Assert.Equal(1.0, result.Generation.TechnicalTermPreservation);
    }

    [Fact]
    public async Task RagEvaluationRunner_HallucinatedCitation_IsDetectedAndFailsGate()
    {
        var c = new RagEvaluationCase(
            Id: "case-cit-hallucinated",
            Question: "Вопрос?",
            RelevantDocumentIds: ["doc-1"],
            ExpectedFacts: ["Alpha"],
            ExpectedTechnicalTerms: ["Alpha"]);

        var fakeChunk = CreateChunk("doc-1", "Alpha fact text");
        // Emits valid [S1] plus fake hallucinated [S99]
        var fakePipeline = new FakePipeline(
            answer: "Ответ Alpha [S1] и выдуманный источник [S99].",
            sources: [new RetrievalResult(fakeChunk, 0.95)]);

        var result = await RagEvaluationRunner.EvaluateAsync(fakePipeline, [c], k: 5);

        Assert.Equal(1, result.Citations.ValidCitations);
        Assert.Equal(1, result.Citations.InvalidCitations);
        Assert.Equal(0.5, result.Citations.CitationValidity, precision: 2);
        Assert.False(result.QualityGatePassed);
        Assert.Contains(result.QualityGateFailures!, f => f.Contains("Citation Validity"));
    }

    [Fact]
    public async Task RagEvaluationRunner_InsufficientContext_HandledGracefully()
    {
        var c = new RagEvaluationCase(
            Id: "case-unanswerable",
            Question: "Неизвестный вопрос?",
            RelevantDocumentIds: [],
            ExpectedFacts: [],
            ExpectInsufficientContext: true);

        var fakePipeline = new FakePipeline(
            answer: "В предоставленном контексте недостаточно информации для ответа.",
            sources: []);

        var result = await RagEvaluationRunner.EvaluateAsync(fakePipeline, [c], k: 5);

        Assert.Equal(1.0, result.Generation.InsufficientContextAccuracy);
        Assert.Equal(1.0, result.Retrieval.HitRateAtK);
        Assert.True(result.QualityGatePassed);
    }

    [Fact]
    public async Task RagEvaluationRunner_InsufficientContext_DoesNotAcceptUnsupportedAnswerWithoutSources()
    {
        var c = new RagEvaluationCase(
            Id: "case-unanswerable-hallucination",
            Question: "Какой пароль Wi-Fi?",
            RelevantDocumentIds: [],
            ExpectedFacts: [],
            ExpectInsufficientContext: true);
        var pipeline = new FakePipeline("Пароль Wi-Fi: admin123.", []);

        var result = await RagEvaluationRunner.EvaluateAsync(pipeline, [c], k: 5);

        Assert.Equal(0.0, result.Generation.InsufficientContextAccuracy);
        Assert.False(result.CaseResults[0].Passed);
        Assert.False(result.QualityGatePassed);
    }

    [Fact]
    public async Task RagEvaluationRunner_FactMatching_DoesNotConfuseNumericIdentifiers()
    {
        var c = new RagEvaluationCase(
            Id: "numeric-identity",
            Question: "Какой регистр?",
            RelevantDocumentIds: ["doc-1"],
            ExpectedFacts: ["40001"]);
        var pipeline = new FakePipeline(
            "Используется регистр 40002 [S1].",
            [new RetrievalResult(CreateChunk("doc-1", "Регистр 40002"), 1.0)]);

        var result = await RagEvaluationRunner.EvaluateAsync(pipeline, [c], k: 5);

        Assert.Equal(0.0, result.Generation.ExpectedFactCoverage);
        Assert.Contains("40001", result.CaseResults[0].MissingFacts);
    }

    [Fact]
    public async Task RagEvaluationRunner_Faithfulness_RequiresClaimsInRetrievedContext()
    {
        var c = new RagEvaluationCase(
            Id: "ungrounded",
            Question: "Какая функция?",
            RelevantDocumentIds: ["doc-1"],
            ExpectedFacts: ["24 В"]);
        var pipeline = new FakePipeline(
            "Для питания требуется 24 В. Устройство поддерживает Wi-Fi 7 [S1].",
            [new RetrievalResult(CreateChunk("doc-1", "Для питания требуется 24 В."), 1.0)]);

        var result = await RagEvaluationRunner.EvaluateAsync(pipeline, [c], k: 5);

        Assert.Equal(1.0, result.Generation.ExpectedFactCoverage);
        Assert.True(result.Generation.FaithfulnessScore < 1.0);
        Assert.False(result.QualityGatePassed);
        Assert.Contains(result.QualityGateFailures!, f => f.Contains("Faithfulness Score"));
    }

    [Fact]
    public async Task RagEvaluationRunner_CitationValidity_RejectsMismatchedProvenance()
    {
        var source = new RetrievalResult(CreateChunk("doc-real", "Alpha"), 1.0);
        var citation = new Citation(1, "doc-invented", "doc-invented#99", null, null, Label: "S1");
        var c = new RagEvaluationCase("citation-provenance", "Q?", ["doc-real"], ExpectedFacts: ["Alpha"]);
        var pipeline = new FakePipeline("Alpha [S1]", [source], [citation]);

        var result = await RagEvaluationRunner.EvaluateAsync(pipeline, [c], k: 5);

        Assert.Equal(0, result.Citations.ValidCitations);
        Assert.Equal(1, result.Citations.InvalidCitations);
        Assert.False(result.CaseResults[0].Passed);
    }

    [Fact]
    public async Task RagEvaluationRunner_Ndcg_ReflectsRelevantResultRank()
    {
        var c = new RagEvaluationCase("ranked", "Q?", ["relevant"]);
        var pipeline = new FakePipeline(
            "Ответ [S2]",
            [CreateHit("noise"), CreateHit("relevant")]);

        var result = await RagEvaluationRunner.EvaluateAsync(pipeline, [c], k: 2);

        Assert.Equal(1.0, result.Retrieval.RecallAtK);
        Assert.Equal(1.0 / Math.Log2(3), result.Retrieval.NdcgAtK, precision: 6);
    }

    // --- 3. EvaluationQualityGate Unit Tests ---

    [Fact]
    public void EvaluationQualityGate_AllThresholdsMet_Passes()
    {
        var result = CreateSyntheticResult(
            hitRate: 0.95, recall: 0.90, mrr: 0.85,
            factCoverage: 0.95, termPreservation: 1.00,
            citationValidity: 1.00, citationCoverage: 0.85);

        var (passed, failures) = EvaluationQualityGate.Evaluate(result, EvaluationThresholds.Default);

        Assert.True(passed);
        Assert.Empty(failures);
    }

    [Fact]
    public void EvaluationQualityGate_Violations_ReportsAllFailures()
    {
        var result = CreateSyntheticResult(
            hitRate: 0.80,         // below 0.90
            recall: 0.70,          // below 0.85
            mrr: 0.60,             // below 0.75
            factCoverage: 0.80,    // below 0.90
            termPreservation: 0.90,// below 1.00
            citationValidity: 0.80,// below 1.00
            citationCoverage: 0.50 // below 0.80
        );

        var (passed, failures) = EvaluationQualityGate.Evaluate(result, EvaluationThresholds.Default);

        Assert.False(passed);
        Assert.Equal(7, failures.Count);
        Assert.Contains(failures, f => f.Contains("HitRate@5"));
        Assert.Contains(failures, f => f.Contains("Recall@5"));
        Assert.Contains(failures, f => f.Contains("MRR"));
        Assert.Contains(failures, f => f.Contains("Expected Fact Coverage"));
        Assert.Contains(failures, f => f.Contains("Technical Term Preservation"));
        Assert.Contains(failures, f => f.Contains("Citation Validity"));
        Assert.Contains(failures, f => f.Contains("Citation Coverage"));
    }

    [Fact]
    public void EvaluationQualityGate_InvalidThresholdsAndMismatchedK_Throw()
    {
        var result = CreateSyntheticResult(1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EvaluationQualityGate.Evaluate(result, EvaluationThresholds.Default with { MinRecallAtK = double.NaN }));
        Assert.Throws<ArgumentException>(() =>
            EvaluationQualityGate.Evaluate(result, EvaluationThresholds.Default with { K = 10 }));
    }

    [Fact]
    public void RagEvaluationResult_SerializationAndSummary_ProducesNonEmptyOutputs()
    {
        var result = CreateSyntheticResult(1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0);

        var summary = result.ToSummaryString();
        var json = result.ToJson();

        Assert.Contains("Pigmentor RAG Evaluation Report", summary);
        Assert.Contains("\"qualityGatePassed\": true", json);
    }

    // --- Helpers ---

    private static RetrievalResult CreateHit(string docId, double score = 1.0)
    {
        var chunk = CreateChunk(docId, docId);
        return new RetrievalResult(chunk, score);
    }

    private static DocumentChunk CreateChunk(string docId, string text, string? chunkId = null)
    {
        var meta = new DocumentMetadata(
            Id: docId,
            Silo: "technical-docs",
            Department: "Engineering",
            Office: "HQ",
            ConfidentialityLevel: "Internal",
            DocumentType: "Manual",
            FiscalYear: 2026,
            Author: "Engineer",
            LastModified: new DateOnly(2026, 3, 1),
            Title: docId);

        return new DocumentChunk(
            ChunkId: chunkId ?? $"{docId}#0",
            DocumentId: docId,
            ChunkIndex: 0,
            Text: text,
            StartCharOffset: 0,
            EndCharOffset: text.Length,
            Metadata: meta);
    }

    private static RagEvaluationResult CreateSyntheticResult(
        double hitRate,
        double recall,
        double mrr,
        double factCoverage,
        double termPreservation,
        double citationValidity,
        double citationCoverage)
    {
        var ret = new RetrievalMetrics(recall, 1.0, mrr, 1.0, 5, 10, hitRate);
        var gen = new GenerationMetrics(factCoverage, termPreservation, 1.0, 1.0, 10);
        var cit = new CitationMetrics(citationValidity, citationCoverage, 10, (int)(10 * citationValidity), (int)(10 * (1 - citationValidity)));

        return new RagEvaluationResult(
            TotalCases: 10,
            PassedCases: 10,
            FailedCases: 0,
            Retrieval: ret,
            Generation: gen,
            Citations: cit,
            CaseResults: [],
            QualityGatePassed: true,
            QualityGateFailures: []);
    }

    private sealed class FakePipeline(
        string answer,
        IReadOnlyList<RetrievalResult> sources,
        IReadOnlyList<Citation>? responseCitations = null) : IRagPipeline
    {
        public Task<RagResponse> AskAsync(RagRequest request, CancellationToken cancellationToken = default)
        {
            var citations = new List<Citation>();
            var diag = new RagDiagnostics(
                TotalLatency: TimeSpan.FromMilliseconds(10),
                RetrievedCandidateCount: sources.Count,
                ContextSourceCount: sources.Count,
                ContextTokenCount: 100,
                FilteredCandidateCount: sources.Count,
                RerankedCandidateCount: sources.Count,
                PromptTokenCount: 150,
                ValidCitationCount: sources.Count > 0 ? 1 : 0,
                InvalidCitationCount: 0,
                RetrievalLatency: TimeSpan.FromMilliseconds(5),
                GenerationLatency: TimeSpan.FromMilliseconds(5));

            return Task.FromResult(new RagResponse(answer, responseCitations ?? citations, sources, diag));
        }

        public Task<RagResponse> AskAsync(string question, int? topK = null, CancellationToken cancellationToken = default) =>
            AskAsync(new RagRequest(question, topK: topK), cancellationToken);

        public IAsyncEnumerable<RagStreamEvent> AskStreamingAsync(RagRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<RagStreamEvent> AskStreamingAsync(string question, int? topK = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
