using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Pigmentor.App.Configuration;
using Pigmentor.App.Paths;
using Pigmentor.App.Runtime;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Tokens;
using Pigmentor.Generation;
using Pigmentor.Retrieval;
using Xunit;

namespace Pigmentor.Tests;

public sealed class OnnxCrossEncoderRerankerTests
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static readonly string ModelPath = Path.Combine(FixturesDir, "test_bge_m3_1024.onnx");
    private static readonly string TokenizerPath = Path.Combine(FixturesDir, "test_tokenizer.json");

    private static DocumentChunk CreateChunk(string id, string text)
    {
        var meta = new DocumentMetadata("test", "test", "doc1", "silo1", "pdf", "Ref", 2026, "Author", DateOnly.MinValue, "Title");
        return new DocumentChunk(id, "doc1", 1, text, 0, text.Length, meta);
    }

    [Fact]
    public async Task RerankAsync_WithEmptyOrSingleCandidate_ReturnsImmediately()
    {
        using var reranker = new OnnxCrossEncoderReranker(ModelPath, TokenizerPath);

        // Empty candidates
        var empty = await reranker.RerankAsync("query", [], topK: 5);
        Assert.Empty(empty);

        // Single candidate
        var singleCandidate = new RetrievalResult(CreateChunk("c1", "Modbus text"), 0.5);
        var singleResult = await reranker.RerankAsync("query", [singleCandidate], topK: 5);
        Assert.Single(singleResult);
        Assert.Equal("c1", singleResult[0].Chunk.ChunkId);

        // TopK <= 0
        var zeroTopK = await reranker.RerankAsync("query", [singleCandidate], topK: 0);
        Assert.Empty(zeroTopK);
    }

    [Fact]
    public async Task RerankAsync_ScoresAndRanksCandidates_CalibratedIntoProbabilities()
    {
        using var reranker = new OnnxCrossEncoderReranker(ModelPath, TokenizerPath, maxPairsPerBatch: 2, applySigmoid: true);

        var c1 = new RetrievalResult(CreateChunk("c1", "Как настроить RS-485?"), 0.2);
        var c2 = new RetrievalResult(CreateChunk("c2", "Какой тип у Modbus Holding Register 40001?"), 0.3);
        var c3 = new RetrievalResult(CreateChunk("c3", "4-20 mA 115200"), 0.1);

        var reranked = await reranker.RerankAsync("RS-485", [c1, c2, c3], topK: 2);

        Assert.Equal(2, reranked.Count);

        // Sigmoid calibration: all scores must be strictly in [0, 1]
        Assert.All(reranked, r =>
        {
            Assert.True(r.Score >= 0.0 && r.Score <= 1.0, $"Score {r.Score} was not in [0, 1]");
            Assert.NotNull(r.RerankerScore);
            Assert.Equal(r.Score, r.RerankerScore!.Value);
        });

        // Scores are sorted descending
        Assert.True(reranked[0].Score >= reranked[1].Score);
    }

    [Fact]
    public async Task RerankAsync_PreservesExistingVectorDenseSparseScores()
    {
        using var reranker = new OnnxCrossEncoderReranker(ModelPath, TokenizerPath);

        var vec = new float[] { 0.1f, 0.2f, 0.3f };
        var candidate1 = new RetrievalResult(CreateChunk("c1", "Modbus Holding Register"), 0.25)
        {
            DenseScore = 0.88,
            SparseScore = 12.5,
            Vector = vec
        };
        var candidate2 = new RetrievalResult(CreateChunk("c2", "RS-485"), 0.15)
        {
            DenseScore = 0.72,
            SparseScore = 8.1,
            Vector = vec
        };

        var reranked = await reranker.RerankAsync("Modbus", [candidate1, candidate2], topK: 2);

        Assert.Equal(2, reranked.Count);
        Assert.All(reranked, r =>
        {
            Assert.NotNull(r.DenseScore);
            Assert.NotNull(r.SparseScore);
            Assert.NotNull(r.Vector);
            Assert.True(r.Vector!.Value.Span.SequenceEqual(vec));
            Assert.NotNull(r.RerankerScore);
        });
    }

    [Fact]
    public async Task ScorePairsBatchedAsync_ProcessesMultipleBatchesCorrectly()
    {
        // 5 candidates with batch size 2 -> 3 batches (2 + 2 + 1)
        using var reranker = new OnnxCrossEncoderReranker(ModelPath, TokenizerPath, maxPairsPerBatch: 2);

        var passages = new[]
        {
            "Как настроить RS-485?",
            "Какой тип у Modbus Holding Register 40001?",
            "4-20 mA 115200",
            "Modbus TCP",
            "RS-485 115200"
        };

        var scores = await reranker.ScorePairsBatchedAsync("Modbus", passages);

        Assert.Equal(5, scores.Length);
        Assert.All(scores, s => Assert.True(s >= 0.0f && s <= 1.0f));
    }

    [Fact]
    public async Task Reranker_HandlesLongSequences_TruncatesPassageSafely()
    {
        // Restrict sequence length to 10 tokens to exercise truncation branch
        using var reranker = new OnnxCrossEncoderReranker(ModelPath, TokenizerPath, maxSequenceLength: 10);

        var longText = "Как настроить RS-485 Modbus TCP Holding Register 40001 4-20 mA 115200 " +
                       "Как настроить RS-485 Modbus TCP Holding Register 40001 4-20 mA 115200";

        var candidate = new RetrievalResult(CreateChunk("c-long", longText), 0.5);

        var reranked = await reranker.RerankAsync("RS-485", [candidate], topK: 1);
        Assert.Single(reranked);
    }

    [Fact]
    public void OnnxCrossEncoderReranker_Disposal_IsIdempotent()
    {
        var reranker = new OnnxCrossEncoderReranker(ModelPath, TokenizerPath);
        reranker.Dispose();
        reranker.Dispose(); // second call must not throw
    }

    [Fact]
    public async Task RagPipeline_WithMinRerankerScore_FiltersAndAbstainsCleanly()
    {
        var c1 = new RetrievalResult(CreateChunk("c1", "Modbus protocol"), 0.5);
        var c2 = new RetrievalResult(CreateChunk("c2", "RS-485 specifications"), 0.4);

        var fakeRetriever = new FakeRetriever([c1, c2]);
        var fakeChat = new FakeChatGenerator("No context");
        var fakeReranker = new ThresholdFakeReranker(scoreToAssign: 0.20); // Low cross-encoder score

        var settings = new RagPipelineSettings
        {
            DefaultTopK = 5,
            MinRerankerScore = 0.50 // Threshold higher than candidate score (0.20)
        };

        var pipeline = new RagPipeline(fakeRetriever, fakeChat, reranker: fakeReranker, settings: settings);

        var response = await pipeline.AskAsync("RS-485 protocol?");

        // All candidates fell below MinRerankerScore, so context was pruned and abstention was triggered
        Assert.Empty(response.Sources);
        Assert.Equal(0.20, response.Diagnostics.MaxRerankerScore);
    }

    [Fact]
    public async Task RagPipeline_WithRetrievalPoolSize_FetchesWidePoolAndReranksTopK()
    {
        // 10 candidates in the store
        var candidates = Enumerable.Range(1, 10)
            .Select(i => new RetrievalResult(CreateChunk($"c{i}", $"Text {i}"), 0.1 * i))
            .ToList();

        var fakeRetriever = new FakeRetriever(candidates);
        var fakeChat = new FakeChatGenerator("Answer [S1]");
        var fakeReranker = new RecordingReranker();

        var settings = new RagPipelineSettings
        {
            DefaultTopK = 3,
            RetrievalPoolSize = 8 // Fetch 8, rerank down to top 3
        };

        var pipeline = new RagPipeline(fakeRetriever, fakeChat, reranker: fakeReranker, settings: settings);

        var response = await pipeline.AskAsync("Query", topK: 3);

        // Retriever was called with retrievalPoolLimit = 8
        Assert.NotNull(fakeRetriever.LastRequest);
        Assert.Equal(8, fakeRetriever.LastRequest!.TopK);
        // Reranker received the 8 candidates
        Assert.Equal(8, fakeReranker.ReceivedCandidateCount);
        // Context contains at most topK = 3 sources
        Assert.True(response.Sources.Count <= 3);
    }

    [Fact]
    public void ConfigManager_SupportsRerankerSettings_SetGetValidate()
    {
        var config = new PigmentorConfig();
        Assert.False(config.Reranker?.Enabled);

        // Test setter logic
        var paths = new PigmentorAppPaths();
        var manager = new PigmentorConfigManager(paths);
        config.Reranker ??= new RerankerConfig();
        config.Reranker.Enabled = true;
        config.Reranker.Provider = "Onnx";
        config.Reranker.ModelPath = "models/bge-reranker-v2-m3/model.onnx";
        config.Reranker.TokenizerPath = "models/bge-reranker-v2-m3/tokenizer.json";
        config.Reranker.MaxPairsPerBatch = 8;
        config.Reranker.MaxSequenceLength = 256;
        config.Reranker.MinScore = 0.45;

        Assert.True(config.Reranker.Enabled);
        Assert.Equal("Onnx", config.Reranker.Provider);
        Assert.Equal(8, config.Reranker.MaxPairsPerBatch);
        Assert.Equal(256, config.Reranker.MaxSequenceLength);
        Assert.Equal(0.45, config.Reranker.MinScore);
    }

    private sealed class ThresholdFakeReranker : IReranker
    {
        private readonly double _score;

        public ThresholdFakeReranker(double scoreToAssign) => _score = scoreToAssign;

        public Task<IReadOnlyList<RetrievalResult>> RerankAsync(
            string query,
            IReadOnlyList<RetrievalResult> candidates,
            int topK,
            CancellationToken cancellationToken = default)
        {
            var scored = candidates
                .Select(c => c with { Score = _score, RerankerScore = _score })
                .Take(topK)
                .ToList();
            return Task.FromResult<IReadOnlyList<RetrievalResult>>(scored);
        }
    }
}
