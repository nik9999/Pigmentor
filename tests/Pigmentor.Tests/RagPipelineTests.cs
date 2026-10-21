using Microsoft.Extensions.AI;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;
using Pigmentor.Core.Tokens;
using Pigmentor.Evaluation;
using Pigmentor.Generation;
using Pigmentor.Retrieval;
using Pigmentor.Retrieval.Decorators;
using Pigmentor.Retrieval.Graph;
using Pigmentor.Retrieval.Vectorless;

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
    public async Task SqliteVecStore_ExecutesSuccessfully_WhenFilterProvided()
    {
        using var store = new Pigmentor.VectorStore.SqliteVec.SqliteVecStore("Data Source=:memory:", dimensions: 2, embeddingModel: "test-model");
        var filter = MetadataFilter.Where(m => m.Silo == "Alpha");

        var results = await store.SearchAsync(DummyVector, topK: 5, filter: filter);
        Assert.NotNull(results);
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

        // Set tight budget allowing only ~1-2 chunks
        var settings = new RagPipelineSettings
        {
            Budget = new ContextBudget(MaxTotalTokens: 500, ReservedOutputTokens: 80)
        };

        var pipeline = new RagPipeline(retriever, chat, tokenCounter: counter, settings: settings);
        var response = await pipeline.AskAsync(new RagRequest("Question", topK: 10));

        // Candidate count was 10, but context sources were limited by budget
        Assert.Equal(10, response.Diagnostics.RetrievedCandidateCount);
        Assert.True(response.Diagnostics.ContextSourceCount < 10);
        Assert.Equal(response.Diagnostics.ContextSourceCount, response.Sources.Count);
    }

    [Fact]
    public async Task EndToEnd_CanonicalRagFlow_SuccessfullyExecuted()
    {
        var meta1 = new DocumentMetadata("doc-1", Silo: "Engineering", "", "", "", "Spec", 2026, "Author", DateOnly.MinValue, "Doc 1");
        var meta2 = new DocumentMetadata("doc-2", Silo: "Engineering", "", "", "", "Guide", 2026, "Author", DateOnly.MinValue, "Doc 2");
        var meta3 = new DocumentMetadata("doc-3", Silo: "HR", "", "", "", "Policy", 2026, "Author", DateOnly.MinValue, "Doc 3");

        var candidates = new List<RetrievalResult>
        {
            new(new DocumentChunk("doc-1#0", "doc-1", 0, "Chunk 1 content.", 0, 16, meta1), 0.95),
            new(new DocumentChunk("doc-2#0", "doc-2", 0, "Chunk 2 content.", 0, 16, meta2), 0.90),
            new(new DocumentChunk("doc-3#0", "doc-3", 0, "Chunk 3 content.", 0, 16, meta3), 0.85)
        };

        var retriever = new FakeRetriever(candidates);
        var chat = new FakeChatGenerator("According to [S1] and [S2], the architecture is sound.");
        var counter = new TokenCounter();
        var recordingReranker = new RecordingReranker();

        var pipeline = new RagPipeline(
            retriever,
            chat,
            reranker: recordingReranker,
            tokenCounter: counter,
            settings: new RagPipelineSettings
            {
                DefaultTopK = 5,
                Budget = new ContextBudget(MaxTotalTokens: 1000, ReservedOutputTokens: 200)
            });

        var request = new RagRequest(
            "What is the architecture?",
            topK: 2,
            metadataFilter: MetadataFilter.Where(m => m.Silo == "Engineering"));

        var response = await pipeline.AskAsync(request);

        Assert.Equal("According to [S1] and [S2], the architecture is sound.", response.Answer);
        Assert.Equal(2, response.Sources.Count);
        Assert.Equal(2, response.Citations.Count);
        Assert.Equal("S1", response.Citations[0].Label);
        Assert.Equal("S2", response.Citations[1].Label);
        Assert.Equal("doc-1#0", response.Citations[0].ChunkId);
        Assert.Equal("doc-2#0", response.Citations[1].ChunkId);

        // Diagnostics verification
        Assert.Equal(2, response.Diagnostics.RetrievedCandidateCount);
        Assert.Equal(2, response.Diagnostics.FilteredCandidateCount);
        Assert.Equal(2, response.Diagnostics.RerankedCandidateCount);
        Assert.Equal(2, response.Diagnostics.ContextSourceCount);
        Assert.NotNull(response.Diagnostics.ContextTokenCount);
        Assert.True(response.Diagnostics.ContextTokenCount > 0);
        Assert.True(response.Diagnostics.PromptTokenCount > 0);
        Assert.Equal(2, response.Diagnostics.ValidCitationCount);
        Assert.Equal(0, response.Diagnostics.InvalidCitationCount);
        Assert.Equal(2, response.Diagnostics.CitedTagCount);
        Assert.True(response.Diagnostics.RetrievalLatency >= TimeSpan.Zero);
        Assert.True(response.Diagnostics.GenerationLatency >= TimeSpan.Zero);
        Assert.True(response.Diagnostics.TotalLatency >= TimeSpan.Zero);
    }

    [Fact]
    public async Task NoContext_RetrieverReturnsEmpty_ProducesNoSourcesAndDeterministicMessage()
    {
        var retriever = new FakeRetriever([]);
        var chat = new FakeChatGenerator("No relevant information found.");
        var pipeline = new RagPipeline(retriever, chat);

        var response = await pipeline.AskAsync(new RagRequest("Unknown concept?"));

        Assert.Equal("No relevant information found.", response.Answer);
        Assert.Empty(response.Sources);
        Assert.Empty(response.Citations);
        Assert.Equal(0, response.Diagnostics.RetrievedCandidateCount);
        Assert.Equal(0, response.Diagnostics.ContextSourceCount);
        Assert.Null(response.Diagnostics.ContextTokenCount);
        Assert.Equal(0, response.Diagnostics.ValidCitationCount);
        Assert.Equal(0, response.Diagnostics.InvalidCitationCount);
        Assert.Equal(0, response.Diagnostics.CitedTagCount);
    }

    [Fact]
    public async Task InvalidAndDuplicateCitations_DeterministicValidation()
    {
        var meta1 = new DocumentMetadata("doc-1", "", "", "", "", "Spec", 0, "", DateOnly.MinValue, "Doc 1");
        var meta2 = new DocumentMetadata("doc-2", "", "", "", "", "Spec", 0, "", DateOnly.MinValue, "Doc 2");
        var chunk1 = new DocumentChunk("doc-1#0", "doc-1", 0, "Chunk 1", 0, 7, meta1);
        var chunk2 = new DocumentChunk("doc-2#0", "doc-2", 0, "Chunk 2", 0, 7, meta2);

        var retriever = new FakeRetriever([new RetrievalResult(chunk1, 0.9), new RetrievalResult(chunk2, 0.8)]);
        // Answer has lower case [s1], duplicate [S1], valid [S2], unknown [S99], invalid [S0], and duplicate [S99]
        var chat = new FakeChatGenerator("Fact from [s1] and again [S1]. Another from [S2]. Hallucination [S99], [S0], and repeat [S99].");
        var pipeline = new RagPipeline(retriever, chat);

        var response = await pipeline.AskAsync(new RagRequest("Query"));

        // Only valid distinct citations S1 and S2 must survive in order of first appearance
        Assert.Equal(2, response.Citations.Count);
        Assert.Equal("S1", response.Citations[0].Label);
        Assert.Equal("doc-1#0", response.Citations[0].ChunkId);
        Assert.Equal("S2", response.Citations[1].Label);
        Assert.Equal("doc-2#0", response.Citations[1].ChunkId);

        // Diagnostics
        Assert.Equal(2, response.Diagnostics.ValidCitationCount);
        Assert.Equal(2, response.Diagnostics.InvalidCitationCount); // S99 and S0
        Assert.Equal(4, response.Diagnostics.CitedTagCount);
    }

    [Fact]
    public async Task CyrillicCitationTags_MappedToCanonicalSources()
    {
        var meta = new DocumentMetadata("doc-1", "", "", "", "", "Spec", 0, "", DateOnly.MinValue, "Doc 1");
        var chunk = new DocumentChunk("doc-1#0", "doc-1", 0, "Chunk 1", 0, 7, meta);
        var retriever = new FakeRetriever([new RetrievalResult(chunk, 0.9)]);
        // Russian Cyrillic 'С' (U+0421) in [С1]
        var chat = new FakeChatGenerator("Факт из источника [\u04211].");
        var pipeline = new RagPipeline(retriever, chat);

        var response = await pipeline.AskAsync(new RagRequest("Query"));
        Assert.Single(response.Citations);
        Assert.Equal("S1", response.Citations[0].Label);
        Assert.Equal("doc-1#0", response.Citations[0].ChunkId);
    }

    [Fact]
    public async Task BudgetConstraint_OnlyFittingSourcesAdmitted_AndInvariantHolds()
    {
        var counter = new TokenCounter();
        var metadata = new DocumentMetadata("doc", "", "", "", "", "Ref", 0, "", DateOnly.MinValue, "Doc");
        var candidates = Enumerable.Range(0, 10).Select(i =>
        {
            var chunk = new DocumentChunk($"doc#{i}", "doc", i, $"Detailed content text paragraph for chunk {i}.", 0, 40, metadata);
            return new RetrievalResult(chunk, 1.0 - i * 0.05);
        }).ToList();

        var retriever = new FakeRetriever(candidates);
        var chat = new FakeChatGenerator("Answer text [S1]");

        var budget = new ContextBudget(MaxTotalTokens: 600, ReservedOutputTokens: 100);
        var settings = new RagPipelineSettings { Budget = budget };

        var pipeline = new RagPipeline(retriever, chat, tokenCounter: counter, settings: settings);
        var response = await pipeline.AskAsync(new RagRequest("Question", topK: 10));

        Assert.Equal(10, response.Diagnostics.RetrievedCandidateCount);
        Assert.True(response.Diagnostics.ContextSourceCount < 10);
        Assert.True(response.Diagnostics.ContextSourceCount > 0);
        Assert.NotNull(response.Diagnostics.ContextTokenCount);

        var promptOverhead = counter.CountTokens(RagPromptBuilder.BuildPrompt("Question", RagContext.Empty));
        var contextAllowance = budget.ComputeAvailableContextTokens(promptOverhead);

        // Invariant: ContextTokenCount must not exceed context allowance
        Assert.True(response.Diagnostics.ContextTokenCount <= contextAllowance);
        Assert.True(response.Diagnostics.PromptTokenCount <= budget.MaxInputTokens);
    }

    [Fact]
    public async Task RagResponseInvariant_AllCitationsReferenceContextSources()
    {
        var meta = new DocumentMetadata("doc-1", "", "", "", "", "Ref", 0, "", DateOnly.MinValue, "Doc");
        var chunk1 = new DocumentChunk("doc-1#0", "doc-1", 0, "Alpha", 0, 5, meta);
        var chunk2 = new DocumentChunk("doc-1#1", "doc-1", 1, "Beta", 0, 4, meta);

        var retriever = new FakeRetriever([new RetrievalResult(chunk1, 0.9), new RetrievalResult(chunk2, 0.8)]);
        var chat = new FakeChatGenerator("Reference [S1] and [S2] and unknown [S50].");
        var pipeline = new RagPipeline(retriever, chat);

        var response = await pipeline.AskAsync(new RagRequest("Test"));

        Assert.NotEmpty(response.Citations);
        foreach (var citation in response.Citations)
        {
            Assert.Contains(response.Sources, s => s.Chunk.ChunkId == citation.ChunkId);
            Assert.Contains(response.Sources, s => s.Chunk.DocumentId == citation.DocumentId);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t  ")]
    public async Task EmptyAndWhitespaceAnswer_DoesNotFail_ReturnsEmptyCitations(string emptyReply)
    {
        var meta = new DocumentMetadata("doc-1", "", "", "", "", "Ref", 0, "", DateOnly.MinValue, "Doc");
        var chunk = new DocumentChunk("doc-1#0", "doc-1", 0, "Chunk text", 0, 10, meta);
        var retriever = new FakeRetriever([new RetrievalResult(chunk, 1.0)]);
        var chat = new FakeChatGenerator(emptyReply);
        var pipeline = new RagPipeline(retriever, chat);

        var response = await pipeline.AskAsync(new RagRequest("Test query"));

        Assert.Equal(string.Empty, response.Answer);
        Assert.Empty(response.Citations);
        Assert.Equal(0, response.Diagnostics.ValidCitationCount);
        Assert.Equal(0, response.Diagnostics.InvalidCitationCount);
        Assert.Equal(0, response.Diagnostics.CitedTagCount);
    }

    [Fact]
    public async Task ProviderException_PropagatesWithoutFakeResponse()
    {
        var meta = new DocumentMetadata("doc-1", "", "", "", "", "Ref", 0, "", DateOnly.MinValue, "Doc");
        var chunk = new DocumentChunk("doc-1#0", "doc-1", 0, "Chunk text", 0, 10, meta);
        var retriever = new FakeRetriever([new RetrievalResult(chunk, 1.0)]);

        var expectedException = new InvalidOperationException("Upstream LLM rate limit or connection timeout.");
        var chat = new FakeChatGenerator("dummy", exceptionToThrow: expectedException);
        var pipeline = new RagPipeline(retriever, chat);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.AskAsync(new RagRequest("Query")));
        Assert.Same(expectedException, thrown);
    }

    [Fact]
    public async Task Cancellation_PropagatesDuringRetrievalAndGeneration()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var retriever = new FakeRetriever();
        var chat = new FakeChatGenerator("Answer");
        var pipeline = new RagPipeline(retriever, chat);

        // Pre-cancelled
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.AskAsync(new RagRequest("Query"), cts.Token));

        // Cancelled during retrieval
        var throwingRetriever = new FakeRetriever(exceptionToThrow: new OperationCanceledException());
        var pipeline2 = new RagPipeline(throwingRetriever, chat);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline2.AskAsync(new RagRequest("Query")));

        // Cancelled during generation
        var throwingChat = new FakeChatGenerator("dummy", exceptionToThrow: new OperationCanceledException());
        var pipeline3 = new RagPipeline(retriever, throwingChat);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline3.AskAsync(new RagRequest("Query")));
    }

    [Fact]
    public async Task Streaming_YieldsDeltasAndTerminatesWithRagCompleted()
    {
        var meta = new DocumentMetadata("doc-1", "", "", "", "", "Spec", 0, "", DateOnly.MinValue, "Doc 1");
        var chunk = new DocumentChunk("doc-1#0", "doc-1", 0, "Pigmentor streaming architecture.", 0, 32, meta);

        var retriever = new FakeRetriever([new RetrievalResult(chunk, 1.0)]);
        var streamingChat = new FakeStreamingChatGenerator(["Pigmentor ", "supports ", "streaming ", "[S1]."]);
        var pipeline = new RagPipeline(retriever, streamingChat);

        var events = new List<RagStreamEvent>();
        await foreach (var evt in pipeline.AskStreamingAsync("How does Pigmentor stream?"))
        {
            events.Add(evt);
        }

        // Must have 4 RagTextDelta followed by 1 RagCompleted
        Assert.Equal(5, events.Count);

        var deltas = events.OfType<RagTextDelta>().Select(d => d.Text).ToList();
        Assert.Equal(["Pigmentor ", "supports ", "streaming ", "[S1]."], deltas);

        var completed = Assert.IsType<RagCompleted>(events[^1]);
        Assert.Equal("Pigmentor supports streaming [S1].", completed.Response.Answer);
        Assert.Single(completed.Response.Sources);
        Assert.Single(completed.Response.Citations);
        Assert.Equal("S1", completed.Response.Citations[0].Label);
        Assert.Equal("doc-1#0", completed.Response.Citations[0].ChunkId);
        Assert.Equal(1, completed.Response.Diagnostics.ValidCitationCount);
        Assert.Equal(0, completed.Response.Diagnostics.InvalidCitationCount);
    }

    [Fact]
    public async Task Streaming_ProviderFailure_DoesNotEmitCompleted()
    {
        var meta = new DocumentMetadata("doc-1", "", "", "", "", "Spec", 0, "", DateOnly.MinValue, "Doc 1");
        var chunk = new DocumentChunk("doc-1#0", "doc-1", 0, "Content.", 0, 8, meta);

        var retriever = new FakeRetriever([new RetrievalResult(chunk, 1.0)]);
        var expectedEx = new InvalidOperationException("Stream severed mid-generation");
        var streamingChat = new FakeStreamingChatGenerator(["Delta 1, ", "Delta 2, "], exceptionToThrow: expectedEx, throwAfterChunkIndex: 0);
        var pipeline = new RagPipeline(retriever, streamingChat);

        var events = new List<RagStreamEvent>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var evt in pipeline.AskStreamingAsync("Test"))
            {
                events.Add(evt);
            }
        });

        Assert.Same(expectedEx, ex);
        // We received the first delta, but RagCompleted was never emitted
        Assert.Single(events);
        Assert.IsType<RagTextDelta>(events[0]);
        Assert.DoesNotContain(events, e => e is RagCompleted);
    }

    [Fact]
    public async Task Streaming_Cancellation_StopsEnumeration()
    {
        var meta = new DocumentMetadata("doc-1", "", "", "", "", "Spec", 0, "", DateOnly.MinValue, "Doc 1");
        var chunk = new DocumentChunk("doc-1#0", "doc-1", 0, "Content.", 0, 8, meta);

        var retriever = new FakeRetriever([new RetrievalResult(chunk, 1.0)]);
        var streamingChat = new FakeStreamingChatGenerator(["Chunk 1", "Chunk 2", "Chunk 3"]);
        var pipeline = new RagPipeline(retriever, streamingChat);

        using var cts = new CancellationTokenSource();

        var events = new List<RagStreamEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var evt in pipeline.AskStreamingAsync(new RagRequest("Test"), cts.Token))
            {
                events.Add(evt);
                cts.Cancel(); // cancel after first event
            }
        });

        Assert.NotEmpty(events);
        Assert.DoesNotContain(events, e => e is RagCompleted);
    }

    [Fact]
    public async Task AskStreamingAsync_WorksWithIChatClientConstructor()
    {
        var meta = new DocumentMetadata("doc-1", "", "", "", "", "Title", 0, "", DateOnly.MinValue, "Doc");
        var chunk = new DocumentChunk("doc-1#0", "doc-1", 0, "Chat client content", 0, 19, meta);
        var retriever = new FakeRetriever([new RetrievalResult(chunk, 1.0)]);

        var client = new TestChatClient(
            "Response from client [S1]",
            streamingChunks: ["Response ", "from ", "client ", "[S1]"]);
        var pipeline = new RagPipeline(retriever, client);

        var events = new List<RagStreamEvent>();
        await foreach (var evt in pipeline.AskStreamingAsync("Streaming test"))
        {
            events.Add(evt);
        }

        var deltas = events.OfType<RagTextDelta>().Select(d => d.Text).ToList();
        Assert.Equal(["Response ", "from ", "client ", "[S1]"], deltas);
        var completed = Assert.IsType<RagCompleted>(events[^1]);
        Assert.Equal("Response from client [S1]", completed.Response.Answer);
        Assert.Single(completed.Response.Citations);
    }

    [Fact]
    public void ContextBuilder_Boundary_ExactlyFits()
    {
        var meta = new DocumentMetadata("doc-1", "", "", "", "", "Title", 0, "", DateOnly.MinValue, "Doc Title");
        var chunk = new DocumentChunk("doc-1#0", "doc-1", 0, "Sample content", 0, 14, meta);
        var candidate = new RetrievalResult(chunk, 1.0);

        var exactCounter = new ExactTokenCounter(s => s.Length);
        var builder = new ContextBuilder(exactCounter);

        var probe = builder.BuildContext([candidate], 1000);
        var exactTokens = probe.TokenCount;
        Assert.True(exactTokens > 0);

        var context = builder.BuildContext([candidate], exactTokens);

        Assert.Single(context.Sources);
        Assert.Equal(exactTokens, context.TokenCount);
        Assert.Equal("doc-1#0", context.Sources[0].Chunk.ChunkId);
    }

    [Fact]
    public void ContextBuilder_Boundary_OneTokenOver_Dropped()
    {
        var meta = new DocumentMetadata("doc-1", "", "", "", "", "Title", 0, "", DateOnly.MinValue, "Doc Title");
        var chunk = new DocumentChunk("doc-1#0", "doc-1", 0, "Sample content", 0, 14, meta);
        var candidate = new RetrievalResult(chunk, 1.0);

        var exactCounter = new ExactTokenCounter(s => s.Length);
        var builder = new ContextBuilder(exactCounter, new ContextBuilderOptions
        {
            OversizedBehavior = OversizedChunkBehavior.Drop
        });

        var probe = builder.BuildContext([candidate], 1000);
        var exactTokens = probe.TokenCount;
        Assert.True(exactTokens > 0);

        // One token short of fitting
        var context = builder.BuildContext([candidate], exactTokens - 1);

        Assert.Empty(context.Sources);
        Assert.Equal(0, context.TokenCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void ContextBuilder_Boundary_ZeroAllowance(int zeroOrNegativeAllowance)
    {
        var meta = new DocumentMetadata("doc-1", "", "", "", "", "Title", 0, "", DateOnly.MinValue, "Title");
        var chunk = new DocumentChunk("doc-1#0", "doc-1", 0, "Content", 0, 7, meta);
        var candidate = new RetrievalResult(chunk, 1.0);

        var counter = new TokenCounter();
        var builder = new ContextBuilder(counter);

        var context = builder.BuildContext([candidate], zeroOrNegativeAllowance);

        Assert.Same(RagContext.Empty, context);
        Assert.Empty(context.Sources);
        Assert.Equal(0, context.TokenCount);
    }

    [Fact]
    public void ContextBuilder_Boundary_VeryLongFirstChunk_AllowsSubsequentSmallerChunk()
    {
        var meta = new DocumentMetadata("doc-1", "", "", "", "", "Title", 0, "", DateOnly.MinValue, "Doc Title");
        var bigChunk = new DocumentChunk("big#0", "big", 0, new string('A', 500), 0, 500, meta);
        var smallChunk = new DocumentChunk("small#0", "small", 0, "Small", 0, 5, meta);

        var candidate1 = new RetrievalResult(bigChunk, 1.0);
        var candidate2 = new RetrievalResult(smallChunk, 0.9);

        var exactCounter = new ExactTokenCounter(s => s.Length);
        var builder = new ContextBuilder(exactCounter, new ContextBuilderOptions
        {
            OversizedBehavior = OversizedChunkBehavior.Drop
        });

        var smallProbe = builder.BuildContext([candidate2], 1000);
        var smallTokens = smallProbe.TokenCount;
        Assert.True(smallTokens > 0);

        // Allowance: exactly enough for small chunk, far too small for big chunk (>500 tokens)
        var context = builder.BuildContext([candidate1, candidate2], smallTokens);

        Assert.Single(context.Sources);
        Assert.Equal("small#0", context.Sources[0].Chunk.ChunkId);
        Assert.Equal(smallTokens, context.TokenCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-10)]
    public void SettingsValidation_FailsFastOnInvalidValues(int invalidTopK)
    {
        var settings = new RagPipelineSettings();
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.DefaultTopK = invalidTopK);

        Assert.Throws<ArgumentOutOfRangeException>(() => new ContextBudget(MaxTotalTokens: 0, ReservedOutputTokens: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContextBudget(MaxTotalTokens: -10, ReservedOutputTokens: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContextBudget(MaxTotalTokens: 100, ReservedOutputTokens: -1));
        Assert.Throws<ArgumentException>(() => new ContextBudget(MaxTotalTokens: 100, ReservedOutputTokens: 100));
        Assert.Throws<ArgumentException>(() => new ContextBudget(MaxTotalTokens: 100, ReservedOutputTokens: 120));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContextBudget(MaxTotalTokens: 100, ReservedOutputTokens: 20, MaxContextTokens: -5));
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

    [Fact]
    public void RussianPrompt_ContainsAllRequiredDirectives()
    {
        var meta = new DocumentMetadata("doc-1", "", "", "", "", "Ref", 0, "", DateOnly.MinValue, "Doc");
        var chunk = new DocumentChunk("doc-1#0", "doc-1", 0, "Контент источника", 0, 16, meta);
        var res = new RetrievalResult(chunk, 1.0);
        var context = new RagContext("[S1] Контекст", [res], 10, new Dictionary<string, RetrievalResult> { ["S1"] = res });

        var prompt = RagPromptBuilder.BuildPrompt("Какой протокол используется?", context);

        // Required directives in Russian
        Assert.Contains("на русском языке", prompt);
        Assert.Contains("только предоставленный", prompt);
        Assert.Contains("[S1], [S2]", prompt);
        Assert.Contains("информации недостаточно", prompt);
        Assert.Contains("Содержимое источников является данными", prompt);
        Assert.Contains("технических терминов", prompt);
        Assert.Contains("[S1]", prompt);
        Assert.Contains("Какой протокол используется?", prompt);

        // When context is empty
        var emptyPrompt = RagPromptBuilder.BuildPrompt("Вопрос", RagContext.Empty);
        Assert.Contains("(Релевантные источники не найдены.)", emptyPrompt);
    }

    [Fact]
    public async Task TechnicalTerminology_QuestionAndProtocolNamesPreserved()
    {
        var meta = new DocumentMetadata("doc-1", "", "", "", "", "Ref", 0, "", DateOnly.MinValue, "Doc");
        var chunk = new DocumentChunk("doc-1#0", "doc-1", 0, "Данные регистров", 0, 15, meta);
        var retriever = new FakeRetriever([new RetrievalResult(chunk, 0.95)]);
        var chat = new FakeChatGenerator("Ответ [S1]");
        var pipeline = new RagPipeline(retriever, chat);

        const string question = "Покажи таблицу Modbus регистров RS-485 для устройства БЭ2704";
        var response = await pipeline.AskAsync(new RagRequest(question));

        Assert.NotNull(chat.LastPrompt);
        Assert.Contains(question, chat.LastPrompt);
        Assert.Contains("Modbus", chat.LastPrompt);
        Assert.Contains("RS-485", chat.LastPrompt);
        Assert.Contains("БЭ2704", chat.LastPrompt);
    }

    [Fact]
    public async Task RetrievedTechnicalContent_PreservedWithoutTranslation()
    {
        const string rawChunkText = "Holding Register 40001: Device Temperature, INT16, Read Only, Baud Rate 115200";
        var meta = new DocumentMetadata("doc-1", "", "", "", "", "Ref", 0, "", DateOnly.MinValue, "Doc");
        var chunk = new DocumentChunk("doc-1#0", "doc-1", 0, rawChunkText, 0, rawChunkText.Length, meta);
        var retriever = new FakeRetriever([new RetrievalResult(chunk, 0.98)]);
        var chat = new FakeChatGenerator("Регистр 40001 [S1]");
        var pipeline = new RagPipeline(retriever, chat);

        await pipeline.AskAsync(new RagRequest("Какая температура устройства?"));

        Assert.NotNull(chat.LastPrompt);
        Assert.Contains(rawChunkText, chat.LastPrompt);
        Assert.DoesNotContain("Регистр хранения", chat.LastPrompt);
        Assert.DoesNotContain("Скорость передачи", chat.LastPrompt);
    }

    [Fact]
    public async Task StrictFinalPromptTokenGuard_TrimsOversizedAdmittedSources()
    {
        var meta = new DocumentMetadata("doc-1", "", "", "", "", "Ref", 0, "", DateOnly.MinValue, "Doc");
        var chunk1 = new DocumentChunk("doc-1#0", "doc-1", 0, "Chunk 1 payload", 0, 15, meta);
        var chunk2 = new DocumentChunk("doc-1#1", "doc-1", 1, "Chunk 2 payload", 0, 15, meta);

        var retriever = new FakeRetriever([new RetrievalResult(chunk1, 0.95), new RetrievalResult(chunk2, 0.90)]);
        var chat = new FakeChatGenerator("Ответ [S1]");

        // Budget: MaxTotalTokens = 160, ReservedOutputTokens = 60 => MaxInputTokens = 100
        var budget = new ContextBudget(MaxTotalTokens: 160, ReservedOutputTokens: 60);
        var settings = new RagPipelineSettings { Budget = budget };

        // Custom counter:
        // Individual chunk/block text => 20 tokens.
        // Empty context prompt overhead => 40 tokens.
        // Context allowance = 100 - 40 = 60 tokens (fits both chunks: 20 + 20 <= 60).
        // Full prompt with both chunks => 105 tokens (> 100).
        // Full prompt with 1 chunk => 75 tokens (<= 100).
        var counter = new ExactTokenCounter(text =>
        {
            if (text.Contains("ИНСТРУКЦИИ", StringComparison.Ordinal))
            {
                if (text.Contains(chunk1.Text, StringComparison.Ordinal) && text.Contains(chunk2.Text, StringComparison.Ordinal))
                {
                    return 105;
                }

                if (text.Contains(chunk1.Text, StringComparison.Ordinal) || text.Contains(chunk2.Text, StringComparison.Ordinal))
                {
                    return 75;
                }

                return 40;
            }

            if (text.Contains(chunk1.Text, StringComparison.Ordinal) || text.Contains(chunk2.Text, StringComparison.Ordinal))
            {
                return 20;
            }

            return 1;
        });

        var pipeline = new RagPipeline(retriever, chat, tokenCounter: counter, settings: settings);
        var response = await pipeline.AskAsync(new RagRequest("Вопрос", topK: 2));

        Assert.Single(response.Sources);
        Assert.Equal("doc-1#0", response.Sources[0].Chunk.ChunkId);
        Assert.Equal(1, response.Diagnostics.ContextSourceCount);
        Assert.Equal(75, response.Diagnostics.PromptTokenCount);
        Assert.True(response.Diagnostics.PromptTokenCount <= budget.MaxInputTokens);
        Assert.NotNull(chat.LastPrompt);
        Assert.Contains(chunk1.Text, chat.LastPrompt);
        Assert.DoesNotContain(chunk2.Text, chat.LastPrompt);
        Assert.Contains("[S1] Название:", chat.LastPrompt);
        Assert.DoesNotContain("[S2] Название:", chat.LastPrompt);
    }

    [Fact]
    public async Task OversizedBasePrompt_FailsFastBeforeProviderCall_BothSyncAndStreaming()
    {
        var meta = new DocumentMetadata("doc-1", "", "", "", "", "Ref", 0, "", DateOnly.MinValue, "Doc");
        var chunk = new DocumentChunk("doc-1#0", "doc-1", 0, "Chunk payload", 0, 13, meta);
        var retriever = new FakeRetriever([new RetrievalResult(chunk, 0.95)]);
        var chat = new FakeChatGenerator("Ответ");

        // Tiny budget where base prompt (~393 tokens) cannot fit into MaxInputTokens: 100 - 30 = 70
        var tinyBudget = new ContextBudget(MaxTotalTokens: 100, ReservedOutputTokens: 30);
        var settings = new RagPipelineSettings { Budget = tinyBudget };
        var pipeline = new RagPipeline(retriever, chat, settings: settings);

        // 1. AskAsync fails fast before chat provider call
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.AskAsync(new RagRequest("Вопрос")));
        Assert.Contains("exceeds the maximum input budget limit", ex.Message);
        Assert.Equal(0, chat.CallCount);

        // 2. AskStreamingAsync fails fast before chat provider call
        var streamingEx = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in pipeline.AskStreamingAsync(new RagRequest("Вопрос")))
            {
            }
        });
        Assert.Contains("exceeds the maximum input budget limit", streamingEx.Message);
        Assert.Equal(0, chat.CallCount);
    }

    [Fact]
    public void RepositoryWide_PromptTemplates_AreInRussian()
    {
        // 1. RagPromptBuilder
        var ragPrompt = RagPromptBuilder.BuildPrompt("Вопрос", RagContext.Empty);
        Assert.Contains("русском языке", ragPrompt);
        Assert.Contains("контекст", ragPrompt);

        // 2. HydeRetriever
        Assert.Contains("гипотетический", HydeRetriever.Prompt);
        Assert.Contains("фрагмент", HydeRetriever.Prompt);

        // 3. RagFusionRetriever
        Assert.Contains("альтернативных вариантов", RagFusionRetriever.Prompt);
        Assert.Contains("следующего вопроса", RagFusionRetriever.Prompt);

        // 4. StepBackRetriever
        Assert.Contains("более общий", StepBackRetriever.Prompt);
        Assert.Contains("концептуальный", StepBackRetriever.Prompt);

        // 5. CragRetriever
        var cragPrompt = CragRetriever.BuildGradePrompt("Вопрос", "Контекст");
        Assert.Contains("Оцени, насколько фрагмент текста отвечает на вопрос", cragPrompt);
        Assert.Contains("CORRECT", cragPrompt);

        // 6. EntityExtractor
        Assert.Contains("сущности", EntityExtractor.Prompt);
        Assert.Contains("связи", EntityExtractor.Prompt);

        // 7. LlmSectionRouter
        var routerPrompt = LlmSectionRouter.BuildPrompt("Разделы", "Вопрос", 3);
        Assert.Contains("наиболее релевантные разделы", routerPrompt);

        // 8. StructuralRetriever
        var structuralPrompt = StructuralRetriever.BuildPrompt("Вопрос");
        Assert.Contains("структурный идентификатор", structuralPrompt);

        // 9. GenerationEvaluator
        var evalPrompt = GenerationEvaluator.BuildPrompt("Контекст", "Утверждение");
        Assert.Contains("Оцени утверждение", evalPrompt);
        Assert.Contains("SUPPORTED", evalPrompt);
    }
}

internal sealed class RecordingReranker : IReranker
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

internal sealed class UnsupportedFilterRetriever : IRetriever
{
    public string Strategy => "unsupported";

    public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(string query, int topK, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<RetrievalResult>>([]);
    }
}

internal sealed class FakeEmbeddingService : IEmbeddingService
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

internal sealed class FakeRetriever : IRetriever
{
    private readonly IReadOnlyList<RetrievalResult> _results;
    private readonly Exception? _exceptionToThrow;

    public FakeRetriever(IReadOnlyList<RetrievalResult>? results = null, Exception? exceptionToThrow = null)
    {
        _results = results ?? [];
        _exceptionToThrow = exceptionToThrow;
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
        if (_exceptionToThrow != null)
        {
            throw _exceptionToThrow;
        }

        LastRequest = request;
        var matches = request.Filter is not null
            ? _results.Where(r => request.Filter.Matches(r.Chunk.Metadata))
            : _results;
        return Task.FromResult<IReadOnlyList<RetrievalResult>>(matches.Take(request.TopK).ToList());
    }
}

internal sealed class FakeChatGenerator : IChatGenerator
{
    private readonly string _reply;
    private readonly Exception? _exceptionToThrow;

    public string? LastPrompt { get; private set; }
    public int CallCount { get; private set; }

    public FakeChatGenerator(string reply, Exception? exceptionToThrow = null)
    {
        _reply = reply;
        _exceptionToThrow = exceptionToThrow;
    }

    public Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastPrompt = prompt;
        cancellationToken.ThrowIfCancellationRequested();
        if (_exceptionToThrow != null)
        {
            throw _exceptionToThrow;
        }
        return Task.FromResult(_reply);
    }
}

internal sealed class FakeStreamingChatGenerator : IChatGenerator
{
    private readonly List<string> _chunks;
    private readonly Exception? _exceptionToThrow;
    private readonly int _throwAfterChunkIndex;

    public string? LastPrompt { get; private set; }
    public int CallCount { get; private set; }

    public FakeStreamingChatGenerator(
        IEnumerable<string> chunks,
        Exception? exceptionToThrow = null,
        int throwAfterChunkIndex = -1)
    {
        _chunks = chunks.ToList();
        _exceptionToThrow = exceptionToThrow;
        _throwAfterChunkIndex = throwAfterChunkIndex;
    }

    public Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastPrompt = prompt;
        cancellationToken.ThrowIfCancellationRequested();
        if (_exceptionToThrow != null && _throwAfterChunkIndex < 0)
        {
            throw _exceptionToThrow;
        }
        return Task.FromResult(string.Concat(_chunks));
    }

    public async IAsyncEnumerable<string> GenerateStreamingAsync(
        string prompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastPrompt = prompt;
        cancellationToken.ThrowIfCancellationRequested();
        for (var i = 0; i < _chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return _chunks[i];
            if (_exceptionToThrow != null && i == _throwAfterChunkIndex)
            {
                throw _exceptionToThrow;
            }
        }
        if (_exceptionToThrow != null && _throwAfterChunkIndex < 0)
        {
            throw _exceptionToThrow;
        }
    }
}

internal sealed class TestChatClient : IChatClient
{
    private readonly string _reply;
    private readonly List<string>? _streamingChunks;

    public TestChatClient(string reply, IEnumerable<string>? streamingChunks = null)
    {
        _reply = reply;
        _streamingChunks = streamingChunks?.ToList();
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

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_streamingChunks != null)
        {
            foreach (var chunk in _streamingChunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            }
        }
        else
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, _reply);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

internal sealed class ExactTokenCounter : ITokenCounter
{
    private readonly Func<string, int>? _customCounter;

    public ExactTokenCounter() { }

    public ExactTokenCounter(Func<string, int> customCounter)
    {
        _customCounter = customCounter;
    }

    public string EncodingName => "exact-test";

    public int CountTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _customCounter != null ? _customCounter(text) : text.Length;
    }

    public int CountTokens(ReadOnlySpan<char> text) => CountTokens(text.ToString());
}
