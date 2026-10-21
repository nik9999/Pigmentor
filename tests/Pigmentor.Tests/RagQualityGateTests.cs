using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pigmentor.Core.Abstractions;
using Pigmentor.Evaluation.Datasets;
using Pigmentor.Evaluation.Quality;
using Pigmentor.Evaluation.Runner;
using Pigmentor.Generation;
using Pigmentor.Ingestion.Chunking;
using Pigmentor.Ingestion.Lifecycle;
using Pigmentor.Ingestion.Services;
using Pigmentor.Retrieval;
using Pigmentor.Tests.Fixtures;
using Pigmentor.VectorStore.SqliteVec;
using Xunit;
using Xunit.Abstractions;

namespace Pigmentor.Tests;

public sealed class RagQualityGateTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _tempDirectory;
    private readonly string _tempDbPath;
    private readonly string _connectionString;

    public RagQualityGateTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"pigmentor_eval_gate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _tempDbPath = Path.Combine(_tempDirectory, "eval_quality_gate.db");
        _connectionString = $"Data Source={_tempDbPath}";
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    [Fact]
    public async Task V1EvaluationDataset_PassesQualityGateAcrossAllMetrics()
    {
        // 1. Arrange Ingestion Infrastructure
        const int dimensions = 256;
        var embedder = new DeterministicSemanticEmbeddingService(dimensions);
        var chatClient = new DeterministicRagChatClient();

        var vecOptions = new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = dimensions,
            EmbeddingModel = embedder.EmbeddingModel
        };

        using var vectorStore = new SqliteVecStore(vecOptions);
        using var stateStore = new SqliteIngestionStateStore(_connectionString);

        var chunker = new FixedSizeChunker(chunkSize: 500, overlap: 50);
        var ingestionService = new DocumentIngestionService(
            stateStore: stateStore,
            vectorStore: vectorStore,
            chunker: chunker,
            embedder: embedder,
            logger: NullLogger<DocumentIngestionService>.Instance);

        // 2. Ingest the Canonical V1 Evaluation Corpus
        var corpus = V1EvaluationCorpus.GetDocuments();
        foreach (var doc in corpus)
        {
            var res = await ingestionService.IngestAsync(doc);
            Assert.Equal(IngestionStatus.Succeeded, res.Status);
            Assert.True(res.ChunksIndexed > 0);
        }

        // 3. Assemble RAG Pipeline
        var retriever = new DenseRetriever(embedder, vectorStore);
        var pipeline = new RagPipeline(retriever, chatClient);

        // 4. Execute Full Evaluation Suite across all 20 Cases
        var cases = V1EvaluationDataset.GetCases();
        var result = await RagEvaluationRunner.EvaluateAsync(
            pipeline: pipeline,
            cases: cases,
            k: 5,
            thresholds: EvaluationThresholds.Default);

        // 5. Output Diagnostic Reports
        _output.WriteLine(result.ToSummaryString());
        _output.WriteLine(string.Empty);
        _output.WriteLine("=== JSON REPORT ===");
        _output.WriteLine(result.ToJson());

        // 6. Assert Overall Invariants
        Assert.Equal(20, result.TotalCases);
        Assert.Equal(20, result.PassedCases);
        Assert.Equal(0, result.FailedCases);
        Assert.Equal(18, result.Retrieval.QueryCount);
        Assert.All(
            result.CaseResults.Where(c => c.CaseId is "insufficient-context-wifi-019" or "insufficient-context-ip-020"),
            c =>
            {
                Assert.False(c.RetrievalEvaluated);
                Assert.True(c.InsufficientContextHandled);
                Assert.True(c.Passed);
            });
        Assert.True(result.QualityGatePassed,
            $"Quality gate failed: {string.Join("; ", result.QualityGateFailures ?? [])}");
        Assert.Empty(result.QualityGateFailures ?? []);

        // 7. Assert Threshold Invariants (§66, §67, §71, §75)
        Assert.True(result.Retrieval.HitRateAtK >= 0.90,
            $"HitRate@{result.Retrieval.K} ({result.Retrieval.HitRateAtK:F2}) < 0.90");
        Assert.True(result.Retrieval.RecallAtK >= 0.85,
            $"Recall@{result.Retrieval.K} ({result.Retrieval.RecallAtK:F2}) < 0.85");
        Assert.True(result.Retrieval.Mrr >= 0.75,
            $"MRR ({result.Retrieval.Mrr:F2}) < 0.75");

        Assert.True(result.Generation.ExpectedFactCoverage >= 0.90,
            $"FactCoverage ({result.Generation.ExpectedFactCoverage:F2}) < 0.90");
        Assert.Equal(1.00, result.Generation.TechnicalTermPreservation);
        Assert.Equal(1.00, result.Generation.InsufficientContextAccuracy);

        Assert.Equal(1.00, result.Citations.CitationValidity);
        Assert.True(result.Citations.CitationCoverage >= 0.80,
            $"CitationCoverage ({result.Citations.CitationCoverage:F2}) < 0.80");
        Assert.Equal(0, result.Citations.InvalidCitations);
    }
}
