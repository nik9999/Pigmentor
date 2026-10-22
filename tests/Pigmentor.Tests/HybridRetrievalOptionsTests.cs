using Microsoft.Extensions.DependencyInjection;
using Pigmentor.App.Configuration;
using Pigmentor.App.Paths;
using Pigmentor.App.Runtime;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;
using Pigmentor.Retrieval;
using Xunit;

namespace Pigmentor.Tests;

public sealed class HybridRetrievalOptionsTests
{
    private static DocumentChunk CreateChunk(string id, string text)
    {
        var meta = new DocumentMetadata("test", "test", "doc1", "silo1", "pdf", "Ref", 2026, "Author", DateOnly.MinValue, "Title");
        return new DocumentChunk(id, "doc1", 0, text, 0, text.Length, meta);
    }

    [Fact]
    public async Task HybridRetriever_Uses50CandidatesPerLegByDefault()
    {
        var denseRecorder = new RecordingRetriever("dense");
        var sparseRecorder = new RecordingRetriever("sparse");
        var hybrid = new HybridRetriever(denseRecorder, sparseRecorder);

        // Caller asks for topK = 8
        await hybrid.RetrieveAsync("query", 8);

        Assert.NotNull(denseRecorder.LastRequest);
        Assert.NotNull(sparseRecorder.LastRequest);

        // Default candidate pool per leg is 50, not 16!
        Assert.Equal(50, denseRecorder.LastRequest.TopK);
        Assert.Equal(50, sparseRecorder.LastRequest.TopK);
    }

    [Fact]
    public async Task HybridRetriever_RespectsConfiguredCandidatePoolPerLeg()
    {
        var denseRecorder = new RecordingRetriever("dense");
        var sparseRecorder = new RecordingRetriever("sparse");

        var options = new HybridRetrievalOptions
        {
            CandidatePoolPerLeg = 100
        };
        var hybrid = new HybridRetriever(denseRecorder, sparseRecorder, options);

        await hybrid.RetrieveAsync("query", 10);

        Assert.Equal(100, denseRecorder.LastRequest!.TopK);
        Assert.Equal(100, sparseRecorder.LastRequest!.TopK);
    }

    [Fact]
    public async Task HybridRetriever_ScalesUpCandidateK_WhenTopKExceedsPool()
    {
        var denseRecorder = new RecordingRetriever("dense");
        var sparseRecorder = new RecordingRetriever("sparse");

        var options = new HybridRetrievalOptions
        {
            CandidatePoolPerLeg = 50
        };
        var hybrid = new HybridRetriever(denseRecorder, sparseRecorder, options);

        // Caller asks for topK = 65, which is > 50
        await hybrid.RetrieveAsync("query", 65);

        Assert.Equal(65, denseRecorder.LastRequest!.TopK);
        Assert.Equal(65, sparseRecorder.LastRequest!.TopK);
    }

    [Fact]
    public async Task HybridRetriever_AppliesWeightedRrf_ToRankings()
    {
        var chunkA = CreateChunk("chunk-A", "Text A");
        var chunkB = CreateChunk("chunk-B", "Text B");

        // Leg 1 (dense): chunkA is rank 1, chunkB is rank 2
        var dense = new StaticRetriever([
            new RetrievalResult(chunkA, 0.9) { DenseScore = 0.9 },
            new RetrievalResult(chunkB, 0.8) { DenseScore = 0.8 }
        ]);

        // Leg 2 (sparse): chunkB is rank 1, chunkA is rank 2
        var sparse = new StaticRetriever([
            new RetrievalResult(chunkB, 15.0) { SparseScore = 15.0 },
            new RetrievalResult(chunkA, 10.0) { SparseScore = 10.0 }
        ]);

        // Test with heavy DenseWeight (2.0 vs 0.5) -> chunkA must win
        var denseFavored = new HybridRetriever(dense, sparse, new HybridRetrievalOptions
        {
            DenseWeight = 2.0,
            SparseWeight = 0.5
        });

        var resultsA = await denseFavored.RetrieveAsync("query", 2);
        Assert.Equal("chunk-A", resultsA[0].Chunk.ChunkId);

        // Test with heavy SparseWeight (0.5 vs 2.0) -> chunkB must win
        var sparseFavored = new HybridRetriever(dense, sparse, new HybridRetrievalOptions
        {
            DenseWeight = 0.5,
            SparseWeight = 2.0
        });

        var resultsB = await sparseFavored.RetrieveAsync("query", 2);
        Assert.Equal("chunk-B", resultsB[0].Chunk.ChunkId);
    }

    [Fact]
    public async Task HybridRetriever_WhenOneLegTimesOut_SalvagesCompletedLeg()
    {
        var chunk = CreateChunk("chunk-dense", "Dense result");
        var dense = new StaticRetriever([new RetrievalResult(chunk, 0.95)]);
        var hangingSparse = new HangingRetriever(TimeSpan.FromSeconds(5));

        var hybrid = new HybridRetriever(dense, hangingSparse, new HybridRetrievalOptions
        {
            LegTimeout = TimeSpan.FromMilliseconds(50) // Short timeout for test
        });

        var results = await hybrid.RetrieveAsync("query", 5);

        // Salvaged results from the fast dense leg
        Assert.Single(results);
        Assert.Equal("chunk-dense", results[0].Chunk.ChunkId);
    }

    [Fact]
    public async Task HybridRetriever_WhenBothLegsTimeOut_ThrowsTimeoutException()
    {
        var hangingDense = new HangingRetriever(TimeSpan.FromSeconds(5));
        var hangingSparse = new HangingRetriever(TimeSpan.FromSeconds(5));

        var hybrid = new HybridRetriever(hangingDense, hangingSparse, new HybridRetrievalOptions
        {
            LegTimeout = TimeSpan.FromMilliseconds(50)
        });

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => hybrid.RetrieveAsync("query", 5));
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public void HybridRetrievalOptions_ValidatesConstraints()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HybridRetrievalOptions { CandidatePoolPerLeg = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new HybridRetrievalOptions { CandidatePoolPerLeg = -5 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new HybridRetrievalOptions { RrfK = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new HybridRetrievalOptions { DenseWeight = -0.1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new HybridRetrievalOptions { SparseWeight = -0.5 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new HybridRetrievalOptions { LegTimeout = TimeSpan.Zero });
    }

    [Fact]
    public async Task ConfigManager_SupportsRetrievalSettings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pigmentor_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = new PigmentorAppPaths(tempDir);
            var manager = new PigmentorConfigManager(paths);

            await manager.SetSettingAsync("retrieval.candidatePoolPerLeg", "80");
            await manager.SetSettingAsync("retrieval.rrfK", "45");
            await manager.SetSettingAsync("retrieval.denseWeight", "1.5");
            await manager.SetSettingAsync("retrieval.sparseWeight", "0.75");
            await manager.SetSettingAsync("retrieval.legTimeoutSeconds", "15");

            var loaded = await manager.LoadAsync();
            Assert.NotNull(loaded.Retrieval);
            Assert.Equal(80, loaded.Retrieval.CandidatePoolPerLeg);
            Assert.Equal(45, loaded.Retrieval.RrfK);
            Assert.Equal(1.5, loaded.Retrieval.DenseWeight);
            Assert.Equal(0.75, loaded.Retrieval.SparseWeight);
            Assert.Equal(15.0, loaded.Retrieval.LegTimeoutSeconds);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private sealed class RecordingRetriever : IRetriever
    {
        public string Strategy { get; }
        public RetrievalRequest? LastRequest { get; private set; }

        public RecordingRetriever(string strategy = "rec") => Strategy = strategy;

        public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(string query, int topK, CancellationToken cancellationToken = default)
            => RetrieveAsync(new RetrievalRequest(query, topK), cancellationToken);

        public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult<IReadOnlyList<RetrievalResult>>([]);
        }
    }

    private sealed class StaticRetriever : IRetriever
    {
        private readonly IReadOnlyList<RetrievalResult> _results;

        public StaticRetriever(IReadOnlyList<RetrievalResult> results) => _results = results;

        public string Strategy => "static";

        public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(string query, int topK, CancellationToken cancellationToken = default)
            => RetrieveAsync(new RetrievalRequest(query, topK), cancellationToken);

        public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RetrievalResult>>(_results.Take(request.TopK).ToList());
    }

    private sealed class HangingRetriever : IRetriever
    {
        private readonly TimeSpan _delay;

        public HangingRetriever(TimeSpan delay) => _delay = delay;

        public string Strategy => "hanging";

        public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(string query, int topK, CancellationToken cancellationToken = default)
            => RetrieveAsync(new RetrievalRequest(query, topK), cancellationToken);

        public async Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            return [];
        }
    }
}
