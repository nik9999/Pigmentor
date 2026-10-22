using Pigmentor.App.Configuration;
using Pigmentor.App.Paths;
using Pigmentor.App.Runtime;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Generation;
using Pigmentor.Retrieval;
using Xunit;

namespace Pigmentor.Tests;

public sealed class NearDuplicateDeduplicationTests
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static readonly string ModelPath = Path.Combine(FixturesDir, "test_bge_m3_1024.onnx");
    private static readonly string TokenizerPath = Path.Combine(FixturesDir, "test_tokenizer.json");

    private static DocumentChunk CreateChunk(string chunkId, string docId, string text)
    {
        var meta = new DocumentMetadata(
            Id: docId,
            Silo: "technical-docs",
            Title: docId);

        return new DocumentChunk(chunkId, docId, 0, text, 0, text.Length, meta);
    }

    [Fact]
    public void DeduplicateNearDuplicates_SuppressesCrossDocumentNearDuplicates_WhenCosineAboveCeiling()
    {
        // Candidate 1: doc1, vector [1, 0, 0]
        var r1 = new RetrievalResult(CreateChunk("doc1#1", "doc1", "Руководство по эксплуатации ПИМФ.426439.004 РЭ"), 0.95)
        {
            Vector = new float[] { 1f, 0f, 0f }
        };

        // Candidate 2: doc2, vector [0.99, 0.01, 0] -> cosine ~ 0.9999 >= 0.94
        var r2 = new RetrievalResult(CreateChunk("doc2#1", "doc2", "Паспорт изделия ПИМФ.426439.004 ПС"), 0.92)
        {
            Vector = new float[] { 0.99f, 0.01f, 0f }
        };

        // Candidate 3: doc3, vector [0, 1, 0] -> cosine 0.0 < 0.94
        var r3 = new RetrievalResult(CreateChunk("doc3#1", "doc3", "Регламент технического обслуживания PX-400"), 0.85)
        {
            Vector = new float[] { 0f, 1f, 0f }
        };

        var deduplicated = NearDuplicateDeduplicator.DeduplicateNearDuplicates([r1, r2, r3], similarityCeiling: 0.94);

        Assert.Equal(2, deduplicated.Count);
        Assert.Equal("doc1#1", deduplicated[0].Chunk.ChunkId);
        Assert.Equal("doc3#1", deduplicated[1].Chunk.ChunkId);
    }

    [Fact]
    public void DeduplicateNearDuplicates_PreservesSameDocumentChunks_EvenWithHighCosineSimilarity()
    {
        // Two consecutive chunks from the same document with identical or near-identical vectors
        var r1 = new RetrievalResult(CreateChunk("doc1#1", "doc1", "Раздел 1: Введение и технические данные"), 0.90)
        {
            Vector = new float[] { 1f, 0f, 0f }
        };
        var r2 = new RetrievalResult(CreateChunk("doc1#2", "doc1", "Раздел 1: Введение и технические данные продолжение"), 0.88)
        {
            Vector = new float[] { 0.99f, 0.02f, 0f }
        };

        // By default crossDocumentOnly = true: both chunks within doc1 must be preserved
        var keptCrossDoc = NearDuplicateDeduplicator.DeduplicateNearDuplicates([r1, r2], similarityCeiling: 0.94, crossDocumentOnly: true);
        Assert.Equal(2, keptCrossDoc.Count);
        Assert.Equal("doc1#1", keptCrossDoc[0].Chunk.ChunkId);
        Assert.Equal("doc1#2", keptCrossDoc[1].Chunk.ChunkId);

        // When crossDocumentOnly = false: intra-document near duplicate is suppressed
        var keptGlobal = NearDuplicateDeduplicator.DeduplicateNearDuplicates([r1, r2], similarityCeiling: 0.94, crossDocumentOnly: false);
        Assert.Single(keptGlobal);
        Assert.Equal("doc1#1", keptGlobal[0].Chunk.ChunkId);
    }

    [Fact]
    public void DeduplicateNearDuplicates_SuppressesExactChunkIdDuplicates()
    {
        var r1 = new RetrievalResult(CreateChunk("doc1#1", "doc1", "Текст чанка 1"), 0.90);
        var r2 = new RetrievalResult(CreateChunk("doc1#1", "doc1", "Текст чанка 1 повтор"), 0.80);

        var result = NearDuplicateDeduplicator.DeduplicateNearDuplicates([r1, r2]);

        Assert.Single(result);
        Assert.Equal(0.90, result[0].Score);
    }

    [Fact]
    public void DeduplicateNearDuplicates_SuppressesExactTextDuplicates_WhenVectorsMissing()
    {
        // Missing vectors across distinct documents, but same text content
        var r1 = new RetrievalResult(CreateChunk("doc1#1", "doc1", "   Настройка интерфейса RS-485: скорость 115200   "), 0.90);
        var r2 = new RetrievalResult(CreateChunk("doc2#1", "doc2", "настройка интерфейса RS-485: скорость 115200"), 0.85);
        var r3 = new RetrievalResult(CreateChunk("doc3#1", "doc3", "Параметры аналогового входа 4-20 мА"), 0.70);

        var result = NearDuplicateDeduplicator.DeduplicateNearDuplicates([r1, r2, r3]);

        Assert.Equal(2, result.Count);
        Assert.Equal("doc1#1", result[0].Chunk.ChunkId);
        Assert.Equal("doc3#1", result[1].Chunk.ChunkId);
    }

    [Fact]
    public void DeduplicateNearDuplicates_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => NearDuplicateDeduplicator.DeduplicateNearDuplicates(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => NearDuplicateDeduplicator.DeduplicateNearDuplicates([], similarityCeiling: 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NearDuplicateDeduplicator.DeduplicateNearDuplicates([], similarityCeiling: -0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => NearDuplicateDeduplicator.DeduplicateNearDuplicates([], similarityCeiling: 1.05));

        // Empty and single items return immediately
        Assert.Empty(NearDuplicateDeduplicator.DeduplicateNearDuplicates([]));

        var single = new RetrievalResult(CreateChunk("doc1#1", "doc1", "Текст"), 1.0);
        var singleResult = NearDuplicateDeduplicator.DeduplicateNearDuplicates([single]);
        Assert.Single(singleResult);
    }

    [Fact]
    public async Task OnnxCrossEncoderReranker_NearDuplicateSuppression_PullsInNextUniqueCandidate()
    {
        using var rerankerWithSuppression = new OnnxCrossEncoderReranker(
            ModelPath,
            TokenizerPath,
            enableNearDuplicateSuppression: true,
            nearDuplicateSimilarityCeiling: 0.94);

        using var rerankerWithoutSuppression = new OnnxCrossEncoderReranker(
            ModelPath,
            TokenizerPath,
            enableNearDuplicateSuppression: false);

        // c1: doc1, RS-485
        var c1 = new RetrievalResult(CreateChunk("doc1#1", "doc1", "Как настроить RS-485 на скорости 115200?"), 0.5)
        {
            Vector = new float[] { 1f, 0f, 0f }
        };

        // c2: doc2, RS-485 near duplicate across document (cosine 0.9999 >= 0.94)
        var c2 = new RetrievalResult(CreateChunk("doc2#1", "doc2", "Как настроить RS-485 на скорости 115200?"), 0.45)
        {
            Vector = new float[] { 0.99f, 0.01f, 0f }
        };

        // c3: doc3, different topic (orthogonal vector)
        var c3 = new RetrievalResult(CreateChunk("doc3#1", "doc3", "Какой тип у Modbus Holding Register 40001?"), 0.4)
        {
            Vector = new float[] { 0f, 1f, 0f }
        };

        // Rerank top 2
        var rerankedWithSuppression = await rerankerWithSuppression.RerankAsync("RS-485", [c1, c2, c3], topK: 2);
        var rerankedWithoutSuppression = await rerankerWithoutSuppression.RerankAsync("RS-485", [c1, c2, c3], topK: 2);

        // Without suppression: top-2 are c1 and c2 (both RS-485)
        Assert.Equal(2, rerankedWithoutSuppression.Count);
        Assert.Equal("doc1#1", rerankedWithoutSuppression[0].Chunk.ChunkId);
        Assert.Equal("doc2#1", rerankedWithoutSuppression[1].Chunk.ChunkId);

        // With suppression: c2 is suppressed as cross-document duplicate of c1, so c3 is pulled into top-2!
        Assert.Equal(2, rerankedWithSuppression.Count);
        Assert.Equal("doc1#1", rerankedWithSuppression[0].Chunk.ChunkId);
        Assert.Equal("doc3#1", rerankedWithSuppression[1].Chunk.ChunkId);
    }

    [Fact]
    public async Task RagPipeline_NearDuplicateSuppression_IntegratesEndToEnd()
    {
        var c1 = new RetrievalResult(CreateChunk("doc1#1", "doc1", "ПИМФ.426439.004 РЭ описание протокола RS-485"), 0.95)
        {
            Vector = new float[] { 1f, 0f, 0f }
        };
        var c2 = new RetrievalResult(CreateChunk("doc2#1", "doc2", "ПИМФ.426439.004 ПС описание протокола RS-485"), 0.90)
        {
            Vector = new float[] { 0.99f, 0.01f, 0f }
        };
        var c3 = new RetrievalResult(CreateChunk("doc3#1", "doc3", "Регламент калибровки аналогового входа"), 0.85)
        {
            Vector = new float[] { 0f, 1f, 0f }
        };

        var fakeRetriever = new FakeRetriever([c1, c2, c3]);
        var fakeChat = new FakeChatGenerator("Ответ на основе источника [S1] и [S2].");

        var settings = new RagPipelineSettings
        {
            DefaultTopK = 2,
            RetrievalPoolSize = 5,
            EnableNearDuplicateSuppression = true,
            NearDuplicateSimilarityCeiling = 0.94
        };

        var pipeline = new RagPipeline(fakeRetriever, fakeChat, settings: settings);
        var response = await pipeline.AskAsync("RS-485 протокол", topK: 2);

        // Context must contain c1 and c3 (c2 was suppressed as duplicate of c1)
        Assert.Equal(2, response.Sources.Count);
        Assert.Equal("doc1#1", response.Sources[0].Chunk.ChunkId);
        Assert.Equal("doc3#1", response.Sources[1].Chunk.ChunkId);
    }

    [Fact]
    public void RagPipelineSettings_NearDuplicateConfiguration_Validation()
    {
        var settings = new RagPipelineSettings();
        Assert.True(settings.EnableNearDuplicateSuppression);
        Assert.Equal(0.94, settings.NearDuplicateSimilarityCeiling);

        settings.NearDuplicateSimilarityCeiling = 0.85;
        Assert.Equal(0.85, settings.NearDuplicateSimilarityCeiling);

        Assert.Throws<ArgumentOutOfRangeException>(() => settings.NearDuplicateSimilarityCeiling = 0.0);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.NearDuplicateSimilarityCeiling = -0.1);
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.NearDuplicateSimilarityCeiling = 1.01);
    }

    [Fact]
    public async Task ConfigManager_SupportsNearDuplicateSettings_SetGetValidate()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pigmentor-dup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var paths = new PigmentorAppPaths(tempDir);
            var manager = new PigmentorConfigManager(paths);

            await manager.SetSettingAsync("retrieval.enablenearduplicatesuppression", "false");
            await manager.SetSettingAsync("retrieval.nearduplicatesimilarityceiling", "0.91");

            var loaded = await manager.LoadAsync();
            Assert.NotNull(loaded.Retrieval);
            Assert.False(loaded.Retrieval.EnableNearDuplicateSuppression);
            Assert.Equal(0.91, loaded.Retrieval.NearDuplicateSimilarityCeiling);

            // Invalid values
            await Assert.ThrowsAsync<ArgumentException>(() =>
                manager.SetSettingAsync("retrieval.enablenearduplicatesuppression", "not-a-bool"));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                manager.SetSettingAsync("retrieval.nearduplicatesimilarityceiling", "0.0"));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                manager.SetSettingAsync("retrieval.nearduplicatesimilarityceiling", "1.5"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
