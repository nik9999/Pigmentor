using Microsoft.Extensions.AI;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;
using Pigmentor.Core.Tokens;
using Pigmentor.Generation;
using Pigmentor.Retrieval;

namespace Pigmentor.Tests;

public sealed class RagPipelineTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\n\r")]
    public void RagRequest_EmptyOrWhitespaceQuestion_ThrowsArgumentException(string invalidQuestion)
    {
        Assert.Throws<ArgumentException>(() => new RagRequest(invalidQuestion));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void RagRequest_NonPositiveTopK_ThrowsArgumentOutOfRangeException(int invalidTopK)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RagRequest("Valid question?", topK: invalidTopK));
    }

    [Fact]
    public async Task AskAsync_NullRequest_ThrowsArgumentNullException()
    {
        var retriever = new FakeRetriever();
        var chat = new FakeChatGenerator("OK");
        var pipeline = new RagPipeline(retriever, chat);

        await Assert.ThrowsAsync<ArgumentNullException>(() => pipeline.AskAsync((RagRequest)null!));
    }

    [Fact]
    public async Task AskAsync_SuccessfulRetrievalAndGeneration_PopulatesResponseAndCitations()
    {
        var metadata = new DocumentMetadata(
            Id: "doc-1",
            Silo: "technical-docs",
            Department: "Engineering",
            Office: "Montreal",
            ConfidentialityLevel: "Internal",
            DocumentType: "Specification",
            FiscalYear: 2026,
            Author: "Dev Team",
            LastModified: new DateOnly(2026, 1, 1),
            Title: "Pigmentor Architecture");

        var chunk = new DocumentChunk(
            ChunkId: "doc-1#0",
            DocumentId: "doc-1",
            ChunkIndex: 0,
            Text: "Pigmentor is an enterprise RAG platform.",
            StartCharOffset: 0,
            EndCharOffset: 41,
            Metadata: metadata);

        var retriever = new FakeRetriever([new RetrievalResult(chunk, 0.95)]);
        var chat = new FakeChatGenerator("Pigmentor is a platform [S1].");
        var counter = new TokenCounter();

        var pipeline = new RagPipeline(retriever, chat, counter);

        var request = new RagRequest("What is Pigmentor?", topK: 3);
        var response = await pipeline.AskAsync(request);

        Assert.Equal("Pigmentor is a platform [S1].", response.Answer);
        Assert.Single(response.Sources);
        Assert.Single(response.Citations);

        var citation = response.Citations[0];
        Assert.Equal(1, citation.SourceIndex);
        Assert.Equal("S1", citation.Label);
        Assert.Equal("doc-1", citation.DocumentId);
        Assert.Equal("doc-1#0", citation.ChunkId);
        Assert.Equal("Pigmentor Architecture", citation.Title);
        Assert.Equal("Pigmentor is an enterprise RAG platform.", citation.Snippet);
        Assert.Same(metadata, citation.Metadata);

        Assert.Equal(1, response.Diagnostics.RetrievedCandidateCount);
        Assert.Equal(1, response.Diagnostics.ContextSourceCount);
        Assert.NotNull(response.Diagnostics.ContextTokenCount);
        Assert.True(response.Diagnostics.ContextTokenCount > 0);
        Assert.True(response.Diagnostics.TotalLatency >= TimeSpan.Zero);
        Assert.Equal(response.Diagnostics.TotalLatencyMs, response.LatencyMs);
    }

    [Fact]
    public async Task AskAsync_InvalidCitation_IgnoredAndRejected()
    {
        var metadata = new DocumentMetadata("doc-1", "", "", "", "", "Spec", 0, "", DateOnly.MinValue, "Title");
        var chunk = new DocumentChunk("doc-1#0", "doc-1", 0, "Chunk content.", 0, 14, metadata);

        var retriever = new FakeRetriever([new RetrievalResult(chunk, 1.0)]);
        // The LLM cites [S1] (valid) and hallucinates [S99], [S0], [XYZ]
        var chat = new FakeChatGenerator("Statement from [S1], hallucinated from [S99] and [XYZ] and [S0].");
        var pipeline = new RagPipeline(retriever, chat);

        var response = await pipeline.AskAsync(new RagRequest("Test query"));

        // Only S1 must survive; S99, XYZ, S0 are rejected
        Assert.Single(response.Citations);
        Assert.Equal("S1", response.Citations[0].Label);
        Assert.Equal("doc-1#0", response.Citations[0].ChunkId);
    }

    [Fact]
    public async Task AskAsync_EmptyRetrievalResult_ProducesEmptyCitationsAndSourcesWithoutFailure()
    {
        var retriever = new FakeRetriever([]);
        var chat = new FakeChatGenerator("I don't know based on the provided context.");
        var pipeline = new RagPipeline(retriever, chat);

        var response = await pipeline.AskAsync(new RagRequest("Unknown query?"));

        Assert.Equal("I don't know based on the provided context.", response.Answer);
        Assert.Empty(response.Sources);
        Assert.Empty(response.Citations);
        Assert.Equal(0, response.Diagnostics.RetrievedCandidateCount);
        Assert.Equal(0, response.Diagnostics.ContextSourceCount);
        Assert.Null(response.Diagnostics.ContextTokenCount);
    }

    [Fact]
    public async Task AskAsync_PropagatesCancellationToken_ThrowsWhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var retriever = new FakeRetriever();
        var chat = new FakeChatGenerator("Never reached");
        var pipeline = new RagPipeline(retriever, chat);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.AskAsync(new RagRequest("Question"), cts.Token));
    }

    [Fact]
    public async Task AskAsync_SourcePreservation_PreservesOriginalChunksAndMetadata()
    {
        var metadata = new DocumentMetadata("doc-99", "silo", "dept", "office", "Internal", "Report", 2026, "Author", DateOnly.MinValue, "Report Title");
        var chunk = new DocumentChunk("doc-99#4", "doc-99", 4, "Important fact.", 100, 115, metadata)
        {
            Provenance = "sha256:abcd"
        };
        var expectedResult = new RetrievalResult(chunk, 0.88);

        var retriever = new FakeRetriever([expectedResult]);
        var chat = new FakeChatGenerator("Answer text [S1]");
        var pipeline = new RagPipeline(retriever, chat);

        var response = await pipeline.AskAsync(new RagRequest("Question"));

        Assert.Same(expectedResult, response.Sources[0]);
        Assert.Same(chunk, response.Sources[0].Chunk);
        Assert.Equal("sha256:abcd", response.Sources[0].Chunk.Provenance);
        Assert.Equal("doc-99#4", response.Citations[0].ChunkId);
    }

    [Fact]
    public async Task AskAsync_MetadataFilter_FiltersSourcesAndAdjustsDiagnostics()
    {
        var hrMeta = new DocumentMetadata("hr-1", "hr-policies", "HR", "Paris", "Internal", "Policy", 2026, "HR", DateOnly.MinValue, "HR Guide");
        var engMeta = new DocumentMetadata("eng-1", "technical-docs", "Engineering", "Paris", "Internal", "Spec", 2026, "Eng", DateOnly.MinValue, "Eng Spec");

        var hrChunk = new DocumentChunk("hr-1#0", "hr-1", 0, "HR content", 0, 10, hrMeta);
        var engChunk = new DocumentChunk("eng-1#0", "eng-1", 0, "Eng content", 0, 11, engMeta);

        var retriever = new FakeRetriever([
            new RetrievalResult(hrChunk, 0.9),
            new RetrievalResult(engChunk, 0.8)
        ]);
        var chat = new FakeChatGenerator("Answer [S1]");
        var pipeline = new RagPipeline(retriever, chat);

        var filter = MetadataFilter.Where(m => m.Department == "HR");
        var request = new RagRequest("Vacation policy?", metadataFilter: filter);

        var response = await pipeline.AskAsync(request);

        // Pre-filter narrowed candidates in retrieval layer to 1 matching HR candidate
        Assert.Equal(1, response.Diagnostics.RetrievedCandidateCount);
        Assert.Equal(1, response.Diagnostics.ContextSourceCount);
        Assert.Single(response.Citations);
        Assert.Equal("hr-1", response.Citations[0].DocumentId);
        Assert.Single(response.Sources);
        Assert.Equal("hr-1", response.Sources[0].Chunk.DocumentId);
    }

    [Fact]
    public async Task AskAsync_WorksWithIChatClientConstructor()
    {
        var metadata = new DocumentMetadata("doc-1", "", "", "", "", "Ref", 0, "", DateOnly.MinValue, "Doc");
        var chunk = new DocumentChunk("doc-1#0", "doc-1", 0, "Hello world", 0, 11, metadata);
        var retriever = new FakeRetriever([new RetrievalResult(chunk, 1.0)]);

        var chatClient = new TestChatClient("Generated response from IChatClient [S1]");
        var pipeline = new RagPipeline(retriever, chatClient);

        var response = await pipeline.AskAsync("Hello?");

        Assert.Equal("Generated response from IChatClient [S1]", response.Answer);
        Assert.Single(response.Citations);
    }

    [Fact]
    public void ContextBuilder_RespectsTokenBudgetAndOutputReserve()
    {
        var counter = new TokenCounter();
        var builder = new ContextBuilder(counter);

        var metadata = new DocumentMetadata("doc-1", "", "", "", "", "Ref", 0, "", DateOnly.MinValue, "Doc");
        var candidates = Enumerable.Range(1, 10).Select(i =>
        {
            var chunk = new DocumentChunk($"doc-{i}#0", $"doc-{i}", 0, $"This is chunk number {i} providing valuable domain information.", 0, 50, metadata);
            return new RetrievalResult(chunk, 1.0 - i * 0.05);
        }).ToList();

        // 100 max total tokens with 40 reserved for output = 60 available context tokens
        var budget = new ContextBudget(MaxTotalTokens: 100, ReservedOutputTokens: 40);
        var context = builder.BuildContext(candidates, budget);

        Assert.True(context.TokenCount <= budget.AvailableTokens);
        Assert.True(context.Sources.Count < candidates.Count);
        Assert.True(context.Sources.Count > 0);
    }

    [Fact]
    public void ContextBuilder_OversizedChunk_TruncatedWhenConfigured()
    {
        var counter = new TokenCounter();
        var options = new ContextBuilderOptions
        {
            OversizedBehavior = OversizedChunkBehavior.Truncate
        };
        var builder = new ContextBuilder(counter, options);

        var metadata = new DocumentMetadata("doc-huge", "", "", "", "", "Ref", 0, "", DateOnly.MinValue, "Huge");
        var hugeText = string.Join(" ", Enumerable.Repeat("enterprise RAG indexing system architecture token budgeting", 50));
        var chunk = new DocumentChunk("doc-huge#0", "doc-huge", 0, hugeText, 0, hugeText.Length, metadata);
        var candidate = new RetrievalResult(chunk, 0.99);

        var budget = new ContextBudget(MaxTotalTokens: 60, ReservedOutputTokens: 10); // 50 available tokens
        var context = builder.BuildContext([candidate], budget);

        Assert.Single(context.Sources);
        Assert.True(context.TokenCount <= budget.AvailableTokens);
        Assert.EndsWith("...", context.Sources[0].Chunk.Text);
    }

    [Fact]
    public void ContextBuilder_OversizedChunk_DroppedWhenConfigured()
    {
        var counter = new TokenCounter();
        var options = new ContextBuilderOptions
        {
            OversizedBehavior = OversizedChunkBehavior.Drop
        };
        var builder = new ContextBuilder(counter, options);

        var metadata = new DocumentMetadata("doc-huge", "", "", "", "", "Ref", 0, "", DateOnly.MinValue, "Huge");
        var hugeText = string.Join(" ", Enumerable.Repeat("enterprise RAG indexing system architecture token budgeting", 50));
        var chunk = new DocumentChunk("doc-huge#0", "doc-huge", 0, hugeText, 0, hugeText.Length, metadata);
        var candidate = new RetrievalResult(chunk, 0.99);

        var budget = new ContextBudget(MaxTotalTokens: 60, ReservedOutputTokens: 10); // 50 available tokens
        var context = builder.BuildContext([candidate], budget);

        Assert.Empty(context.Sources);
        Assert.Equal(0, context.TokenCount);
    }

    [Fact]
    public void ContextOrdering_InterleavesSourcesInContext()
    {
        var counter = new TokenCounter();
        var options = new ContextBuilderOptions { EnableInterleaving = true };
        var builder = new ContextBuilder(counter, options);

        var metadata = new DocumentMetadata("doc", "", "", "", "", "Ref", 0, "", DateOnly.MinValue, "Doc");
        var candidates = Enumerable.Range(0, 5).Select(i =>
        {
            var chunk = new DocumentChunk($"doc#{i}", "doc", i, $"Content for rank {i}", 0, 20, metadata);
            return new RetrievalResult(chunk, 10.0 - i);
        }).ToList();

        var context = builder.BuildContext(candidates, 1000);

        Assert.Equal(5, context.Sources.Count);
        // Interleaved order: even ranks go to front (0, 2, 4), odd ranks go to back (3, 1) -> [0, 2, 4, 3, 1]
        Assert.Equal("doc#0", context.Sources[0].Chunk.ChunkId);
        Assert.Equal("doc#2", context.Sources[1].Chunk.ChunkId);
        Assert.Equal("doc#4", context.Sources[2].Chunk.ChunkId);
        Assert.Equal("doc#3", context.Sources[3].Chunk.ChunkId);
        Assert.Equal("doc#1", context.Sources[4].Chunk.ChunkId);

        // Deterministic tags S1..S5
        Assert.True(context.SourceMap.ContainsKey("S1"));
        Assert.True(context.SourceMap.ContainsKey("S5"));
        Assert.Same(context.Sources[0], context.SourceMap["S1"]);
        Assert.Same(context.Sources[4], context.SourceMap["S5"]);
    }

    [Fact]
    public async Task MetadataFilter_IsPassedToRetrievalLayer()
    {
        var metadata = new DocumentMetadata("doc", Silo: "Alpha", "", "", "", "Ref", 0, "", DateOnly.MinValue, "Doc");
        var chunk = new DocumentChunk("doc#1", "doc", 0, "Chunk text", 0, 10, metadata);
        var retriever = new FakeRetriever([new RetrievalResult(chunk, 1.0)]);
        var chat = new FakeChatGenerator("Response [S1]");
        var pipeline = new RagPipeline(retriever, chat);

        var filter = MetadataFilter.Where(m => m.Silo == "Alpha");
        await pipeline.AskAsync(new RagRequest("Question", metadataFilter: filter));

        Assert.NotNull(retriever.LastRequest);
        Assert.Same(filter, retriever.LastRequest.Filter);
    }

    [Fact]
    public async Task MandatoryFilter_AppliedBeforeReranking_RerankerOnlyReceivesFilteredCandidates()
    {
        var metaAlpha = new DocumentMetadata("docA", Silo: "Alpha", "", "", "", "Ref", 0, "", DateOnly.MinValue, "DocA");
        var metaBeta = new DocumentMetadata("docB", Silo: "Beta", "", "", "", "Ref", 0, "", DateOnly.MinValue, "DocB");

        var candidates = new List<RetrievalResult>
        {
            new(new DocumentChunk("docA#1", "docA", 0, "Alpha chunk 1", 0, 10, metaAlpha), 1.0),
            new(new DocumentChunk("docB#1", "docB", 0, "Beta chunk 1", 0, 10, metaBeta), 0.9),
            new(new DocumentChunk("docA#2", "docA", 1, "Alpha chunk 2", 0, 10, metaAlpha), 0.8),
            new(new DocumentChunk("docB#2", "docB", 1, "Beta chunk 2", 0, 10, metaBeta), 0.7)
        };

        var recorder = new RecordingReranker();
        var retriever = new FakeRetriever(candidates);
        var chat = new FakeChatGenerator("Response [S1]");
        var pipeline = new RagPipeline(retriever, chat, reranker: recorder);

        var filter = MetadataFilter.Where(m => m.Silo == "Alpha");
        var response = await pipeline.AskAsync(new RagRequest("Question", metadataFilter: filter, topK: 4));

        // Reranker received ONLY Alpha candidates (2), not the unfiltered 4
        Assert.Equal(2, recorder.ReceivedCandidates.Count);
        Assert.All(recorder.ReceivedCandidates, c => Assert.Equal("Alpha", c.Chunk.Metadata.Silo));
    }

    [Fact]
    public async Task UnsupportedRetriever_ThrowsNotSupportedException_WhenFilterProvided()
    {
        IRetriever retriever = new UnsupportedFilterRetriever();
        var filter = MetadataFilter.Where(m => m.Silo == "Alpha");
        var request = new RetrievalRequest("query", 5, filter);

        await Assert.ThrowsAsync<NotSupportedException>(() => retriever.RetrieveAsync(request));
    }

    private static readonly float[] DummyVector = [1f, 0f];

    [Fact]
    public async Task SqliteVecStore_ThrowsNotSupportedException_WhenFilterProvided()
    {
        using var store = new Pigmentor.VectorStore.SqliteVec.SqliteVecStore("Data Source=:memory:");
        var filter = MetadataFilter.Where(m => m.Silo == "Alpha");

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            store.SearchAsync(DummyVector, topK: 5, filter: filter));
    }

    [Fact]
    public void ContextBudget_PromptOverhead_DeductedFromContextAllowance()
    {
        var budget = new ContextBudget(MaxTotalTokens: 1000, ReservedOutputTokens: 250);

        // Prompt overhead with question and system instructions = 100 tokens
        var allowance = budget.ComputeAvailableContextTokens(promptOverheadTokens: 100);

        // 1000 - 250 - 100 = 650 tokens
        Assert.Equal(650, allowance);
    }

    [Fact]
    public async Task FinalSources_CorrespondStrictlyToContext_NotEntireCandidatePool()
    {
        var counter = new TokenCounter();
        var metadata = new DocumentMetadata("doc", "", "", "", "", "Ref", 0, "", DateOnly.MinValue, "Doc");
        var candidates = Enumerable.Range(0, 10).Select(i =>
        {
            var chunk = new DocumentChunk($"doc#{i}", "doc", i, $"This is chunk text for candidate item {i}.", 0, 40, metadata);
            return new RetrievalResult(chunk, 1.0 - i * 0.05);
        }).ToList();

        var retriever = new FakeRetriever(candidates);
        var chat = new FakeChatGenerator("Answer text [S1]");

        // Set tight budget allowing only ~2-3 chunks
        var settings = new RagPipelineSettings
        {
            Budget = new ContextBudget(MaxTotalTokens: 80, ReservedOutputTokens: 20)
        };

        var pipeline = new RagPipeline(retriever, chat, tokenCounter: counter, settings: settings);
        var response = await pipeline.AskAsync(new RagRequest("Question", topK: 10));

        // Candidate count was 10, but context sources were limited by budget
        Assert.Equal(10, response.Diagnostics.RetrievedCandidateCount);
        Assert.True(response.Diagnostics.ContextSourceCount < 10);
        Assert.Equal(response.Diagnostics.ContextSourceCount, response.Sources.Count);
    }

    [Fact]
    public async Task MmrReranker_PromotesDiversityOverRedundancy()
    {
        var metadata = new DocumentMetadata("doc", "", "", "", "", "Ref", 0, "", DateOnly.MinValue, "Doc");

        // Two identical texts (duplicates) and one novel text
        var chunkA1 = new DocumentChunk("doc#1", "doc", 1, "query topic alpha", 0, 17, metadata);
        var chunkA2 = new DocumentChunk("doc#2", "doc", 2, "query topic alpha", 0, 17, metadata);
        var chunkB = new DocumentChunk("doc#3", "doc", 3, "completely different topic beta", 0, 31, metadata);

        var candidates = new List<RetrievalResult>
        {
            new(chunkA1, 0.95),
            new(chunkA2, 0.94),
            new(chunkB, 0.80)
        };

        var fakeEmbedder = new FakeEmbeddingService();
        var mmrReranker = new MmrReranker(fakeEmbedder, lambda: 0.5);

        var reranked = await mmrReranker.RerankAsync("query", candidates, topK: 2);

        Assert.Equal(2, reranked.Count);
        // Best candidate is A1
        Assert.Equal("doc#1", reranked[0].Chunk.ChunkId);
        // Due to diversity penalty, chunkB must be preferred over duplicate chunkA2
        Assert.Equal("doc#3", reranked[1].Chunk.ChunkId);
    }

    private sealed class RecordingReranker : IReranker
    {
        public int ReceivedCandidateCount => ReceivedCandidates.Count;
        public IReadOnlyList<RetrievalResult> ReceivedCandidates { get; private set; } = [];

        public Task<IReadOnlyList<RetrievalResult>> RerankAsync(
            string query,
            IReadOnlyList<RetrievalResult> candidates,
            int topK,
            CancellationToken cancellationToken = default)
        {
            ReceivedCandidates = candidates;
            return Task.FromResult<IReadOnlyList<RetrievalResult>>(candidates.Take(topK).ToList());
        }
    }

    private sealed class UnsupportedFilterRetriever : IRetriever
    {
        public string Strategy => "unsupported";

        public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(string query, int topK, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<RetrievalResult>>([]);
        }
    }

    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public string EmbeddingModel => "test-model";
        public int Dimensions => 2;

        public Task<EmbeddedChunk> EmbedAsync(DocumentChunk chunk, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new EmbeddedChunk(chunk, GetVector(chunk.Text), EmbeddingModel));
        }

        public async IAsyncEnumerable<EmbeddedChunk> EmbedAsync(
            IAsyncEnumerable<DocumentChunk> chunks,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var chunk in chunks.WithCancellation(cancellationToken))
            {
                yield return await EmbedAsync(chunk, cancellationToken);
            }
        }

        public Task<ReadOnlyMemory<float>> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(GetVector(query));
        }

        private static ReadOnlyMemory<float> GetVector(string text)
        {
            // Simple deterministic vector: alpha points in [1, 0], beta points in [0, 1]
            if (text.Contains("alpha", StringComparison.OrdinalIgnoreCase))
            {
                return new float[] { 1f, 0f };
            }
            if (text.Contains("beta", StringComparison.OrdinalIgnoreCase))
            {
                return new float[] { 0f, 1f };
            }
            return new float[] { 0.707f, 0.707f };
        }
    }

    private sealed class FakeRetriever : IRetriever
    {
        private readonly IReadOnlyList<RetrievalResult> _results;

        public FakeRetriever(IReadOnlyList<RetrievalResult>? results = null)
        {
            _results = results ?? [];
        }

        public string Strategy => "fake";

        public RetrievalRequest? LastRequest { get; private set; }

        public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(string query, int topK, CancellationToken cancellationToken = default)
        {
            return RetrieveAsync(new RetrievalRequest(query, topK), cancellationToken);
        }

        public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            var matches = request.Filter is not null
                ? _results.Where(r => request.Filter.Matches(r.Chunk.Metadata))
                : _results;
            return Task.FromResult<IReadOnlyList<RetrievalResult>>(matches.Take(request.TopK).ToList());
        }
    }

    private sealed class FakeChatGenerator : IChatGenerator
    {
        private readonly string _reply;

        public FakeChatGenerator(string reply)
        {
            _reply = reply;
        }

        public Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_reply);
        }
    }

    private sealed class TestChatClient : IChatClient
    {
        private readonly string _reply;

        public TestChatClient(string reply)
        {
            _reply = reply;
        }

        public ChatClientMetadata Metadata { get; } = new("test");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
