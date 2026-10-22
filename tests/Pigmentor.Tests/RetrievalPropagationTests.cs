using Microsoft.Extensions.AI;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;
using Pigmentor.Retrieval;
using Pigmentor.Retrieval.Decorators;
using Pigmentor.Retrieval.Graph;
using Pigmentor.Retrieval.Hybrid;
using Pigmentor.Retrieval.Vectorless;

namespace Pigmentor.Tests;

public class RetrievalPropagationTests
{
    private static DocumentMetadata CreateMeta(string docId, string dept, string author = "author") =>
        new(
            Id: docId,
            Silo: "silo1",
            Department: dept,
            Office: "office1",
            ConfidentialityLevel: "Internal",
            DocumentType: "Spec",
            FiscalYear: 2026,
            Author: author,
            LastModified: new DateOnly(2026, 1, 1),
            Title: $"Doc {docId}");

    private static DocumentChunk CreateChunk(string id, string text, string dept, string author = "author") =>
        new(
            ChunkId: id,
            DocumentId: id,
            ChunkIndex: 0,
            Text: text,
            StartCharOffset: 0,
            EndCharOffset: text.Length,
            Metadata: CreateMeta(id, dept, author));

    [Fact]
    public async Task SparseRetriever_AppliesMetadataFilter_PreTopK()
    {
        // doc-hr has highest BM25 score for "keyword", but belongs to "HR".
        // doc-eng has lower BM25 score for "keyword", but belongs to "Engineering".
        var docHr = CreateChunk("doc-hr", "keyword keyword keyword keyword keyword", "HR");
        var docEng = CreateChunk("doc-eng", "keyword something else", "Engineering");

        var sparse = new SparseRetriever();
        sparse.Index([docHr, docEng]);

        var filter = MetadataFilter.Where(m => m.Department == "Engineering");

        // Without filter, top 1 is doc-hr.
        var unfiltered = await sparse.RetrieveAsync("keyword", 1);
        Assert.Single(unfiltered);
        Assert.Equal("doc-hr", unfiltered[0].Chunk.ChunkId);

        // With pre-filter for Engineering, doc-hr must be excluded before top-1 truncation.
        var filtered = await sparse.RetrieveAsync(new RetrievalRequest("keyword", 1, filter));
        Assert.Single(filtered);
        Assert.Equal("doc-eng", filtered[0].Chunk.ChunkId);
    }

    [Fact]
    public async Task HybridRetriever_PropagatesRetrievalRequest_ToBothLegs()
    {
        var denseRecorder = new RecordingRetriever("dense");
        var sparseRecorder = new RecordingRetriever("sparse");
        var hybrid = new HybridRetriever(denseRecorder, sparseRecorder);

        var filter = MetadataFilter.Where(m => m.FiscalYear == 2026);
        var request = new RetrievalRequest("hybrid query", 4, filter);

        var results = await hybrid.RetrieveAsync(request);

        Assert.NotNull(denseRecorder.LastRequest);
        Assert.Equal("hybrid query", denseRecorder.LastRequest.Query);
        Assert.Equal(filter, denseRecorder.LastRequest.Filter);
        Assert.True(denseRecorder.LastRequest.TopK >= 8); // Candidate multiplier

        Assert.NotNull(sparseRecorder.LastRequest);
        Assert.Equal("hybrid query", sparseRecorder.LastRequest.Query);
        Assert.Equal(filter, sparseRecorder.LastRequest.Filter);
        Assert.True(sparseRecorder.LastRequest.TopK >= 8);
    }

    [Fact]
    public async Task HybridRetriever_AppliesInferredScope_ToBothLegs()
    {
        var denseRecorder = new RecordingRetriever("dense");
        var sparseRecorder = new RecordingRetriever("sparse");
        var hybrid = new HybridRetriever(denseRecorder, sparseRecorder);

        await hybrid.RetrieveAsync("Какая модель имеет минимальную цену?", 4);

        Assert.NotNull(denseRecorder.LastRequest?.Filter);
        Assert.NotNull(sparseRecorder.LastRequest?.Filter);
        var catalogMeta = CreateMeta("catalog", "sales") with { Silo = "product-catalog" };
        var technicalMeta = CreateMeta("manual", "engineering") with { Silo = "technical-docs" };
        Assert.True(denseRecorder.LastRequest.Filter.Matches(catalogMeta));
        Assert.False(denseRecorder.LastRequest.Filter.Matches(technicalMeta));
        Assert.True(sparseRecorder.LastRequest.Filter.Matches(catalogMeta));
        Assert.False(sparseRecorder.LastRequest.Filter.Matches(technicalMeta));
    }

    [Fact]
    public async Task DenseRetriever_DoesNotInferScope_PassesRequestFilterDirectlyToStore()
    {
        var recordingStore = new RecordingVectorStore();
        var fakeEmbedder = new FakeEmbeddingService();
        var dense = new DenseRetriever(fakeEmbedder, recordingStore);

        // When request has no filter, even if the query text mentions catalog keywords,
        // DenseRetriever must NOT infer scope — it must pass null filter directly to the store.
        await dense.RetrieveAsync("Какая модель имеет минимальную цену?", 4);

        Assert.Null(recordingStore.LastFilter);
        Assert.Equal(4, recordingStore.LastTopK);

        // When request explicitly specifies a filter, it must be forwarded as-is.
        var customFilter = MetadataFilter.Where(m => m.Department == "Engineering");
        await dense.RetrieveAsync(new RetrievalRequest("Какая модель имеет минимальную цену?", 4, customFilter));

        Assert.Same(customFilter, recordingStore.LastFilter);
    }

    [Fact]
    public async Task TableAwareLexicalReranker_PromotesRequestedCurrentTable()
    {
        var meta = CreateMeta("mds", "engineering") with { Silo = "technical-docs" };
        var generic = new RetrievalResult(
            new DocumentChunk("generic", "mds", 0, "MDS AI-8UI. Команды DCON и настройки каналов.", 0, 48, meta),
            1.0);
        var table = new RetrievalResult(
            new DocumentChunk("table", "mds", 1, """
                | Ток | (-20…+20) | мА | ±40 мкА |
                | Ток | (0…20) | мА | ±20 мкА |
                | Ток | (4…20) | мА | ±16 мкА |
                """, 49, 160, meta),
            0.1);
        var reranker = new TableAwareLexicalReranker();

        var result = await reranker.RerankAsync(
            "Назови все токовые диапазоны из таблицы характеристик MDS AI-8UI.",
            [generic, table],
            topK: 2);

        Assert.Equal("table", result[0].Chunk.ChunkId);
        Assert.True(result[0].Score > result[1].Score);
    }

    [Fact]
    public async Task MinScoreFilter_PropagatesRetrievalRequest()
    {
        var chunk1 = CreateChunk("c1", "text", "HR");
        var recorder = new RecordingRetriever("inner", [new RetrievalResult(chunk1, 0.8)]);
        var minScore = new MinScoreFilter(recorder, minScore: 0.5);

        var filter = MetadataFilter.Where(m => m.Department == "HR");
        var request = new RetrievalRequest("score query", 2, filter);

        var results = await minScore.RetrieveAsync(request);

        Assert.Single(results);
        Assert.NotNull(recorder.LastRequest);
        Assert.Equal("score query", recorder.LastRequest.Query);
        Assert.Equal(filter, recorder.LastRequest.Filter);
        Assert.Equal(2, recorder.LastRequest.TopK);
    }

    [Fact]
    public async Task HydeRetriever_PropagatesMetadataFilter_WithRewrittenQuery()
    {
        var chat = new SimpleChatClient("Hypothetical document content about vectors");
        var recorder = new RecordingRetriever("inner");
        var hyde = new HydeRetriever(recorder, chat);

        var filter = MetadataFilter.Where(m => m.Silo == "silo1");
        var request = new RetrievalRequest("user query", 5, filter);

        await hyde.RetrieveAsync(request);

        Assert.NotNull(recorder.LastRequest);
        Assert.Equal("Hypothetical document content about vectors", recorder.LastRequest.Query);
        Assert.Equal(5, recorder.LastRequest.TopK);
        Assert.Equal(filter, recorder.LastRequest.Filter);
    }

    [Fact]
    public async Task RagFusionRetriever_PropagatesMetadataFilter_ToAllQueryVariants()
    {
        var chat = new SimpleChatClient("variant one\nvariant two");
        var recorder = new RecordingRetriever("inner");
        var fusion = new RagFusionRetriever(recorder, chat, queryVariants: 2);

        var filter = MetadataFilter.Where(m => m.DocumentType == "Spec");
        var request = new RetrievalRequest("original query", 3, filter);

        await fusion.RetrieveAsync(request);

        Assert.True(recorder.AllRequests.Count >= 3); // original + 2 variants
        foreach (var req in recorder.AllRequests)
        {
            Assert.Equal(filter, req.Filter);
            Assert.True(req.TopK >= 6); // Candidate multiplier
        }
    }

    [Fact]
    public async Task StepBackRetriever_PropagatesMetadataFilter_ToBothLegs()
    {
        var chat = new SimpleChatClient("generic step back question");
        var recorder = new RecordingRetriever("inner");
        var stepBack = new StepBackRetriever(recorder, chat);

        var filter = MetadataFilter.Where(m => m.ConfidentialityLevel == "Internal");
        var request = new RetrievalRequest("specific question", 4, filter);

        await stepBack.RetrieveAsync(request);

        Assert.Equal(2, recorder.AllRequests.Count); // original leg + step-back leg
        Assert.Equal("specific question", recorder.AllRequests[0].Query);
        Assert.Equal(filter, recorder.AllRequests[0].Filter);

        Assert.Equal("generic step back question", recorder.AllRequests[1].Query);
        Assert.Equal(filter, recorder.AllRequests[1].Filter);
    }

    [Fact]
    public async Task CragRetriever_PropagatesMetadataFilter_ToInnerAndFallback()
    {
        // Chat always responds INCORRECT so Crag invokes WebFallback
        var chat = new SimpleChatClient("INCORRECT");
        var chunk = CreateChunk("c1", "candidate text", "Engineering");
        var innerRecorder = new RecordingRetriever("inner", [new RetrievalResult(chunk, 0.9)]);
        var fallbackRecorder = new RecordingRetriever("fallback");

        var crag = new CragRetriever(innerRecorder, chat)
        {
            WebFallback = fallbackRecorder,
        };

        var filter = MetadataFilter.Where(m => m.Department == "Engineering");
        var request = new RetrievalRequest("eval query", 2, filter);

        await crag.RetrieveAsync(request);

        Assert.NotNull(innerRecorder.LastRequest);
        Assert.Equal("eval query", innerRecorder.LastRequest.Query);
        Assert.Equal(filter, innerRecorder.LastRequest.Filter);
        Assert.Equal(4, innerRecorder.LastRequest.TopK); // 2 * topK candidates

        Assert.NotNull(fallbackRecorder.LastRequest);
        Assert.Equal("eval query", fallbackRecorder.LastRequest.Query);
        Assert.Equal(filter, fallbackRecorder.LastRequest.Filter);
        Assert.Equal(2, fallbackRecorder.LastRequest.TopK);
    }

    [Fact]
    public async Task CragRetriever_UsesFallbackWhenAllCandidatesAreAmbiguous()
    {
        var chat = new SimpleChatClient("AMBIGUOUS");
        var inner = new RecordingRetriever("inner", [new RetrievalResult(CreateChunk("c1", "candidate", "Engineering"), 0.8)]);
        var fallback = new RecordingRetriever("fallback");
        var crag = new CragRetriever(inner, chat) { WebFallback = fallback };

        var results = await crag.RetrieveAsync(new RetrievalRequest("query", 1));

        Assert.Empty(results);
        Assert.NotNull(fallback.LastRequest);
        Assert.Equal("query", fallback.LastRequest.Query);
    }

    [Fact]
    public async Task HybridDatabaseRetriever_PropagatesMetadataFilter_ToBothLegs()
    {
        var vectorRecorder = new RecordingRetriever("vector");
        var graphRecorder = new RecordingRetriever("graph");
        var hybridDb = new HybridDatabaseRetriever(vectorRecorder, graphRecorder);

        var filter = MetadataFilter.Where(m => m.FiscalYear == 2026);
        var request = new RetrievalRequest("hybrid-db query", 3, filter);

        await hybridDb.RetrieveAsync(request);

        Assert.NotNull(vectorRecorder.LastRequest);
        Assert.Equal("hybrid-db query", vectorRecorder.LastRequest.Query);
        Assert.Equal(filter, vectorRecorder.LastRequest.Filter);

        Assert.NotNull(graphRecorder.LastRequest);
        Assert.Equal("hybrid-db query", graphRecorder.LastRequest.Query);
        Assert.Equal(filter, graphRecorder.LastRequest.Filter);
    }

    [Fact]
    public async Task StructuralRetriever_PreFiltersMatchedNodes_WhenExplicitIdMatched()
    {
        var root = new StructuralNode("root", "Root", "Root", "Root document text", null, ["GDPR/Art17", "GDPR/Art18"], []);
        var art17 = new StructuralNode("GDPR/Art17", "GDPR > Art 17", "Art 17", "Right to erasure", "root", [], []);
        var art18 = new StructuralNode("GDPR/Art18", "GDPR > Art 18", "Art 18", "Right to restriction", "root", [], []);
        var index = new StructuralIndex(root, [root, art17, art18]);

        // Chat extracts "GDPR/Art17"
        var chat = new SimpleChatClient("{\"explicitId\": \"GDPR/Art17\", \"topic\": null}");
        var retriever = new StructuralRetriever(index, chat);

        // Matching filter for structural metadata
        var filterMatch = MetadataFilter.Where(m => m.Department == "Structural");
        var reqMatch = new RetrievalRequest("What is GDPR/Art17?", 2, filterMatch);
        var resultsMatch = await retriever.RetrieveAsync(reqMatch);
        Assert.NotEmpty(resultsMatch);
        Assert.Equal("GDPR/Art17", resultsMatch[0].Chunk.ChunkId);

        // Filter that does NOT match structural metadata
        var filterNoMatch = MetadataFilter.Where(m => m.Department == "NonExistentDept");
        var reqNoMatch = new RetrievalRequest("What is GDPR/Art17?", 2, filterNoMatch);
        var resultsNoMatch = await retriever.RetrieveAsync(reqNoMatch);
        Assert.Empty(resultsNoMatch);
    }

    [Fact]
    public async Task StructuralRetriever_PropagatesRequest_ToTopicFallback()
    {
        var root = new StructuralNode("root", "Root", "Root", "Root document text", null, [], []);
        var index = new StructuralIndex(root, [root]);

        // Chat returns no explicit id -> triggers topic fallback
        var chat = new SimpleChatClient("{\"explicitId\": null, \"topic\": \"GDPR\"}");
        var fallbackRecorder = new RecordingRetriever("topic-fallback");
        var retriever = new StructuralRetriever(index, chat, topicFallback: fallbackRecorder);

        var filter = MetadataFilter.Where(m => m.Silo == "silo1");
        var request = new RetrievalRequest("explain privacy rules", 3, filter);

        await retriever.RetrieveAsync(request);

        Assert.NotNull(fallbackRecorder.LastRequest);
        Assert.Equal("explain privacy rules", fallbackRecorder.LastRequest.Query);
        Assert.Equal(filter, fallbackRecorder.LastRequest.Filter);
        Assert.Equal(3, fallbackRecorder.LastRequest.TopK);
    }

    [Fact]
    public async Task UnsupportedRetriever_FailsFast_WhenFilterIsNotNull()
    {
        var root = new StructuralNode("root", "Root", "Root", "Root text", null, [], []);
        var index = new StructuralIndex(root, [root]);
        var chat = new SimpleChatClient("0");
        var filter = MetadataFilter.Where(m => m.FiscalYear == 2026);
        var requestWithFilter = new RetrievalRequest("query", 2, filter);

        // 1. LlmSectionRouter
        var sectionRouter = new LlmSectionRouter(index, chat);
        var exRouter = await Assert.ThrowsAsync<NotSupportedException>(() => sectionRouter.RetrieveAsync(requestWithFilter));
        Assert.Contains("llm-section", exRouter.Message);

        // Must NOT throw on request without filter
        var requestNoFilter = new RetrievalRequest("query", 2);
        var results = await sectionRouter.RetrieveAsync(requestNoFilter);
        Assert.NotNull(results);

        // Old overload must also succeed
        var oldResults = await sectionRouter.RetrieveAsync("query", 2);
        Assert.NotNull(oldResults);

        // 2. GraphRetriever
        var extractor = new EntityExtractor(chat);
        var fakeGraph = new FakeGraphStore();
        var graphRetriever = new GraphRetriever(extractor, fakeGraph);
        var exGraph = await Assert.ThrowsAsync<NotSupportedException>(() => graphRetriever.RetrieveAsync(requestWithFilter));
        Assert.Contains("graph", exGraph.Message);

        // 3. LazyGraphRagRetriever
        var lazyGraph = new LazyGraphRagRetriever(extractor, fakeGraph, chat);
        var exLazy = await Assert.ThrowsAsync<NotSupportedException>(() => lazyGraph.RetrieveAsync(requestWithFilter));
        Assert.Contains("lazy-graph-rag", exLazy.Message);
    }

    [Fact]
    public async Task OldOverload_DelegatesToRetrievalRequest_AcrossRetrievers()
    {
        var recorder = new RecordingRetriever("inner");
        var hyde = new HydeRetriever(recorder, new SimpleChatClient("hypothetical text"));

        var results = await hyde.RetrieveAsync("test query", 3);

        Assert.NotNull(recorder.LastRequest);
        Assert.Equal("hypothetical text", recorder.LastRequest.Query);
        Assert.Equal(3, recorder.LastRequest.TopK);
        Assert.Null(recorder.LastRequest.Filter);
    }

    private sealed class RecordingRetriever : IRetriever
    {
        private readonly IReadOnlyList<RetrievalResult> _results;

        public RecordingRetriever(string strategy, IReadOnlyList<RetrievalResult>? results = null)
        {
            Strategy = strategy;
            _results = results ?? [];
        }

        public string Strategy { get; }

        public RetrievalRequest? LastRequest { get; private set; }
        public List<RetrievalRequest> AllRequests { get; } = [];

        public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
            string query,
            int topK,
            CancellationToken cancellationToken = default) =>
            RetrieveAsync(new RetrievalRequest(query, topK), cancellationToken);

        public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
            RetrievalRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            LastRequest = request;
            AllRequests.Add(request);

            var matches = request.Filter is not null
                ? _results.Where(r => request.Filter.Matches(r.Chunk.Metadata))
                : _results;

            return Task.FromResult<IReadOnlyList<RetrievalResult>>(matches.Take(request.TopK).ToList());
        }
    }

    private sealed class SimpleChatClient : IChatClient
    {
        private readonly string _response;

        public SimpleChatClient(string response)
        {
            _response = response;
        }

        public ChatClientMetadata Metadata { get; } = new("simple-test");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _response)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FakeGraphStore : IGraphStore
    {
        public Task EnsureSchemaExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertEntityAsync(GraphEntity entity, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpsertRelationAsync(GraphRelation relation, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object>>> QueryAsync(string query, IReadOnlyDictionary<string, object>? parameters = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object>>>([]);
        public Task<IReadOnlyList<GraphEntity>> TraverseAsync(IEnumerable<string> entityNames, int maxHops, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GraphEntity>>([]);
    }

    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        private static readonly float[] DefaultVector = [1f, 0f];

        public string EmbeddingModel => "test-model";
        public int Dimensions => 2;

        public Task<EmbeddedChunk> EmbedAsync(DocumentChunk chunk, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddedChunk(chunk, DefaultVector, EmbeddingModel));

        public async IAsyncEnumerable<EmbeddedChunk> EmbedAsync(
            IAsyncEnumerable<DocumentChunk> chunks,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var chunk in chunks.WithCancellation(cancellationToken))
            {
                yield return await EmbedAsync(chunk, cancellationToken);
            }
        }

        public Task<ReadOnlyMemory<float>> EmbedQueryAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<float>>(DefaultVector);
    }

    private sealed class RecordingVectorStore : IVectorStore
    {
        public string CollectionName => "test-collection";
        public MetadataFilter? LastFilter { get; private set; }
        public int LastTopK { get; private set; }

        public Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<RetrievalResult>> SearchAsync(
            ReadOnlyMemory<float> queryVector,
            int topK,
            MetadataFilter? filter = null,
            CancellationToken cancellationToken = default)
        {
            LastFilter = filter;
            LastTopK = topK;
            return Task.FromResult<IReadOnlyList<RetrievalResult>>([]);
        }

        public Task UpsertAsync(IEnumerable<EmbeddedChunk> chunks, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(IEnumerable<string> chunkIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
