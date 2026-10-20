using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;
using Pigmentor.Evaluation.Cases;
using Pigmentor.Evaluation.Runner;
using Pigmentor.Generation;
using Pigmentor.Ingestion.Chunking;
using Pigmentor.Ingestion.Lifecycle;
using Pigmentor.Ingestion.Services;
using Pigmentor.Retrieval;
using Pigmentor.Tests.Fixtures;
using Pigmentor.VectorStore.SqliteVec;
using Xunit;

namespace Pigmentor.Tests;

public sealed class RagRegressionTests : IDisposable
{
    private readonly string _tempDirectory;

    public RagRegressionTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"pigmentor_reg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    private string CreateConnectionString() => $"Data Source={Path.Combine(_tempDirectory, $"test_{Guid.NewGuid():N}.db")}";

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

    private static Document CreateDocument(string id, string content, string department = "Engineering", string title = "Doc")
    {
        var meta = new DocumentMetadata(
            Id: id,
            Silo: "technical-docs",
            Department: department,
            Office: "HQ",
            ConfidentialityLevel: "Internal",
            DocumentType: "Manual",
            FiscalYear: 2026,
            Author: "Engineer",
            LastModified: new DateOnly(2026, 3, 1),
            Title: title);

        return new Document(meta, content, $"{id}.txt");
    }

    [Fact]
    public async Task Regression_UnchangedDocument_SkippedWithoutReEmbedding()
    {
        var connectionString = CreateConnectionString();
        const int dims = 64;
        var embedder = new DeterministicSemanticEmbeddingService(dims);
        var vecOptions = new SqliteVecStoreOptions { ConnectionString = connectionString, Dimensions = dims, EmbeddingModel = embedder.EmbeddingModel };

        using var vectorStore = new SqliteVecStore(vecOptions);
        using var stateStore = new SqliteIngestionStateStore(connectionString);
        var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(100, 0), embedder);

        var doc = CreateDocument("doc-skip", "RS-485 communication line test content.");

        // First ingestion: indexed
        var res1 = await service.IngestAsync(doc);
        Assert.False(res1.Skipped);
        var initialCalls = embedder.CallCount;
        Assert.True(initialCalls > 0);

        // Second ingestion: skipped, zero new embedding calls
        var res2 = await service.IngestAsync(doc);
        Assert.True(res2.Skipped);
        Assert.Equal(initialCalls, embedder.CallCount);
    }

    [Fact]
    public async Task Regression_ContentUpdated_ReindexesAndIncrementsVersion()
    {
        var connectionString = CreateConnectionString();
        const int dims = 64;
        var embedder = new DeterministicSemanticEmbeddingService(dims);
        var vecOptions = new SqliteVecStoreOptions { ConnectionString = connectionString, Dimensions = dims, EmbeddingModel = embedder.EmbeddingModel };

        using var vectorStore = new SqliteVecStore(vecOptions);
        using var stateStore = new SqliteIngestionStateStore(connectionString);
        var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(100, 0), embedder);

        var docV1 = CreateDocument("doc-update", "Initial version 1 content.");
        var res1 = await service.IngestAsync(docV1);
        Assert.Equal(1, res1.Version);

        var docV2 = CreateDocument("doc-update", "Updated version 2 content with new information.");
        var res2 = await service.IngestAsync(docV2);
        Assert.False(res2.Skipped);
        Assert.Equal(2, res2.Version);
    }

    [Fact]
    public async Task Regression_StaleChunkCleanup_LeavesNoOrphans()
    {
        var connectionString = CreateConnectionString();
        const int dims = 64;
        var embedder = new DeterministicSemanticEmbeddingService(dims);
        var vecOptions = new SqliteVecStoreOptions { ConnectionString = connectionString, Dimensions = dims, EmbeddingModel = embedder.EmbeddingModel };

        using var vectorStore = new SqliteVecStore(vecOptions);
        using var stateStore = new SqliteIngestionStateStore(connectionString);
        var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(30, 0), embedder);

        // Long text produces 4 chunks
        var longText = "Chunk one text. Chunk two text. Chunk three text. Chunk four text.";
        var docV1 = CreateDocument("doc-stale", longText);
        var res1 = await service.IngestAsync(docV1);
        Assert.True(res1.ChunksIndexed >= 3);

        // Short text produces only 1 chunk
        var shortText = "Single chunk replacement.";
        var docV2 = CreateDocument("doc-stale", shortText);
        var res2 = await service.IngestAsync(docV2);

        Assert.Equal(res1.ChunksIndexed, res2.ChunksDeleted);
        Assert.Equal(1, res2.ChunksIndexed);

        // State store records only 1 chunk
        var state = await stateStore.GetAsync("doc-stale", "default");
        Assert.NotNull(state);
        Assert.Single(state.ChunkIds);
    }

    [Fact]
    public async Task Regression_DocumentDeletion_CleansUpVectorStoreAndState()
    {
        var connectionString = CreateConnectionString();
        const int dims = 64;
        var embedder = new DeterministicSemanticEmbeddingService(dims);
        var vecOptions = new SqliteVecStoreOptions { ConnectionString = connectionString, Dimensions = dims, EmbeddingModel = embedder.EmbeddingModel };

        using var vectorStore = new SqliteVecStore(vecOptions);
        using var stateStore = new SqliteIngestionStateStore(connectionString);
        var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(100, 0), embedder);

        var doc = CreateDocument("doc-del", "Content to be deleted.");
        await service.IngestAsync(doc);

        var deleted = await service.DeleteAsync("doc-del");
        Assert.True(deleted);

        var state = await stateStore.GetAsync("doc-del", "default");
        Assert.Null(state);

        var queryVec = await embedder.EmbedQueryAsync("Content");
        var searchHits = await vectorStore.SearchAsync(queryVec, 5);
        Assert.DoesNotContain(searchHits, h => h.Chunk.DocumentId == "doc-del");
    }

    [Fact]
    public async Task Regression_RestartStateRestoration_PreservesIngestionState()
    {
        var connectionString = CreateConnectionString();
        const int dims = 64;
        var embedder = new DeterministicSemanticEmbeddingService(dims);
        var doc = CreateDocument("doc-restart", "Persistent state test content.");

        // Session 1: ingest and close
        {
            var vecOptions = new SqliteVecStoreOptions { ConnectionString = connectionString, Dimensions = dims, EmbeddingModel = embedder.EmbeddingModel };
            using var vectorStore = new SqliteVecStore(vecOptions);
            using var stateStore = new SqliteIngestionStateStore(connectionString);
            var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(100, 0), embedder);

            var res1 = await service.IngestAsync(doc);
            Assert.False(res1.Skipped);
        }

        SqliteConnection.ClearAllPools();

        // Session 2: reopen same database, verify skip
        {
            var vecOptions = new SqliteVecStoreOptions { ConnectionString = connectionString, Dimensions = dims, EmbeddingModel = embedder.EmbeddingModel };
            using var vectorStore = new SqliteVecStore(vecOptions);
            using var stateStore = new SqliteIngestionStateStore(connectionString);
            var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(100, 0), embedder);

            var initialCalls = embedder.CallCount;
            var res2 = await service.IngestAsync(doc);

            Assert.True(res2.Skipped);
            Assert.Equal(initialCalls, embedder.CallCount);
        }
    }

    [Fact]
    public async Task Regression_StreamingParity_MatchesNonStreamingAnswerAndCitations()
    {
        var connectionString = CreateConnectionString();
        const int dims = 64;
        var embedder = new DeterministicSemanticEmbeddingService(dims);
        var chatClient = new DeterministicRagChatClient();

        var vecOptions = new SqliteVecStoreOptions { ConnectionString = connectionString, Dimensions = dims, EmbeddingModel = embedder.EmbeddingModel };
        using var vectorStore = new SqliteVecStore(vecOptions);
        using var stateStore = new SqliteIngestionStateStore(connectionString);
        var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(100, 0), embedder);

        var doc = CreateDocument("doc-stream", "Скорость передачи данных интерфейса RS-485 составляет 115200 бит/с.");
        await service.IngestAsync(doc);

        var retriever = new DenseRetriever(embedder, vectorStore);
        var pipeline = new RagPipeline(retriever, chatClient);

        var request = new RagRequest("Какая скорость передачи данных установлена для интерфейса RS-485?");

        // Non-streaming ask
        var nonStreamingResponse = await pipeline.AskAsync(request);

        // Streaming ask
        var streamedText = new StringBuilder();
        RagResponse? finalResponse = null;
        await foreach (var evt in pipeline.AskStreamingAsync(request))
        {
            if (evt is RagTextDelta delta)
            {
                streamedText.Append(delta.Text);
            }
            else if (evt is RagCompleted completed)
            {
                finalResponse = completed.Response;
            }
        }

        // Parity assertions
        Assert.Equal(nonStreamingResponse.Answer, streamedText.ToString());
        Assert.NotNull(finalResponse);
        Assert.Equal(nonStreamingResponse.Citations.Count, finalResponse.Citations.Count);
        Assert.Equal(nonStreamingResponse.Answer, finalResponse.Answer);
    }

    [Fact]
    public async Task Regression_ContextBudgetGuard_TrimsExcessContextWithoutOverflow()
    {
        var connectionString = CreateConnectionString();
        const int dims = 64;
        var embedder = new DeterministicSemanticEmbeddingService(dims);
        var chatClient = new DeterministicRagChatClient();

        var vecOptions = new SqliteVecStoreOptions { ConnectionString = connectionString, Dimensions = dims, EmbeddingModel = embedder.EmbeddingModel };
        using var vectorStore = new SqliteVecStore(vecOptions);
        using var stateStore = new SqliteIngestionStateStore(connectionString);
        var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(80, 0), embedder);

        // Add 5 documents with distinct content
        for (var i = 1; i <= 5; i++)
        {
            await service.IngestAsync(CreateDocument($"doc-budget-{i}", $"Содержимое документа номер {i} с описанием параметров оборудования."));
        }

        var retriever = new DenseRetriever(embedder, vectorStore);
        // Set budget of 500 input tokens (instructions ~397 + room for only 1 trimmed candidate chunk)
        var settings = new RagPipelineSettings
        {
            Budget = new ContextBudget(MaxTotalTokens: 600, ReservedOutputTokens: 100)
        };
        var pipeline = new RagPipeline(retriever, chatClient, settings: settings);

        var response = await pipeline.AskAsync("Оборудование", topK: 5);

        Assert.NotNull(response.Answer);
        Assert.True(response.Diagnostics.PromptTokenCount <= 500,
            $"Prompt token count {response.Diagnostics.PromptTokenCount} exceeded budget 500");
    }

    [Fact]
    public async Task Regression_MetadataFilterIsolation_SegregatesDepartments()
    {
        var connectionString = CreateConnectionString();
        const int dims = 64;
        var embedder = new DeterministicSemanticEmbeddingService(dims);
        var chatClient = new DeterministicRagChatClient();

        var vecOptions = new SqliteVecStoreOptions { ConnectionString = connectionString, Dimensions = dims, EmbeddingModel = embedder.EmbeddingModel };
        using var vectorStore = new SqliteVecStore(vecOptions);
        using var stateStore = new SqliteIngestionStateStore(connectionString);
        var service = new DocumentIngestionService(stateStore, vectorStore, new FixedSizeChunker(100, 0), embedder);

        // Ingest Engineering document
        await service.IngestAsync(CreateDocument("doc-eng", "Монтаж оборудования инженерного отдела.", department: "Engineering"));

        // Ingest HR document
        await service.IngestAsync(CreateDocument("doc-hr", "Трудовой распорядок отдела кадров HR.", department: "HR"));

        var retriever = new DenseRetriever(embedder, vectorStore);
        var pipeline = new RagPipeline(retriever, chatClient);

        // Request with Engineering filter
        var engFilter = MetadataFilter.Where(m => m.Department == "Engineering");
        var engReq = new RagRequest("отдел", metadataFilter: engFilter, topK: 5);
        var engResp = await pipeline.AskAsync(engReq);

        Assert.All(engResp.Sources, s => Assert.Equal("Engineering", s.Chunk.Metadata?.Department));
        Assert.DoesNotContain(engResp.Sources, s => s.Chunk.DocumentId == "doc-hr");
    }
}
