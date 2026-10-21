using System.Globalization;
using Microsoft.Extensions.AI;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Tokens;
using Pigmentor.Generation;
using Pigmentor.Ingestion.Chunking;
using Pigmentor.Ingestion.Normalization.Rules;
using Pigmentor.Retrieval;
using Xunit;

namespace Pigmentor.Tests;

public sealed class DogfoodingCorrectiveHardeningTests
{
    [Fact]
    public void TableContextualization_CarriesParentAncestry_ToMatchDeviceSpecificQuery()
    {
        var meta = new DocumentMetadata(
            Id: "doc-mds-1",
            Silo: "technical-docs",
            Department: "Engineering",
            Office: "Main",
            ConfidentialityLevel: "Internal",
            DocumentType: "Manual",
            FiscalYear: 2026,
            Author: "Author",
            LastModified: new DateOnly(2026, 4, 1),
            Title: "Руководство MDS AI-8UI");

        var provP1 = new BlockProvenance("doc-mds-1", PageNumber: 1);
        var provP6 = new BlockProvenance("doc-mds-1", PageNumber: 6);

        var table = new ExtractedTable(
            TableId: "doc-mds-1#t1",
            Caption: "Таблица 2.1 Пределы погрешности и диапазоны измерений напряжения и тока",
            Columns: ["Тип сигнала", "Диапазон", "Ед. изм."],
            Rows:
            [
                new ExtractedTableRow(0, ["Напряжение", "(-10…+10)", "В"], new Dictionary<string, string>()),
                new ExtractedTableRow(1, ["Ток", "(-20…+20)", "мА"], new Dictionary<string, string>()),
                new ExtractedTableRow(2, ["Ток", "(0…20)", "мА"], new Dictionary<string, string>()),
                new ExtractedTableRow(3, ["Ток", "(4…20)", "мА"], new Dictionary<string, string>())
            ],
            PageStart: 6,
            PageEnd: 6);

        var blocks = new List<ExtractedBlock>
        {
            new HeadingBlock("doc-mds-1#b1", "MDS AI-8UI", 1, provP1),
            new HeadingBlock("doc-mds-1#b2", "MDS AI-8UI/D", 1, provP1),
            new HeadingBlock("doc-mds-1#b3", "2 Технические характеристики", 1, provP6),
            new HeadingBlock("doc-mds-1#b4", "2.1 Характеристики каналов ввода", 2, provP6),
            new TextBlock("doc-mds-1#b5", "Число каналов ввода: 8. Допустимые типы сигналов и диапазоны измерений приведены в таблице 2.1.", provP6),
            new TableBlock("doc-mds-1#b6", table, provP6)
        };

        var normDoc = new NormalizedDocument(
            DocumentId: "doc-mds-1",
            SourcePath: "data/technical-docs/doc-mds-1.pdf",
            Format: ".pdf",
            Blocks: blocks,
            Metadata: meta,
            Diagnostics: new NormalizationDiagnostics());

        var chunker = new StructuralDocumentChunker();
        var chunks = chunker.Chunk(normDoc);

        var tableChunk = chunks.FirstOrDefault(c => c.Text.Contains("(-20…+20)", StringComparison.Ordinal));
        Assert.NotNull(tableChunk);

        // Embedding-visible context must carry document device title, section, subsection, and table caption
        Assert.Contains("MDS AI-8UI", tableChunk.Text, StringComparison.Ordinal);
        Assert.Contains("2 Технические характеристики", tableChunk.Text, StringComparison.Ordinal);
        Assert.Contains("2.1 Характеристики каналов ввода", tableChunk.Text, StringComparison.Ordinal);
        Assert.Contains("Таблица 2.1", tableChunk.Text, StringComparison.Ordinal);
        Assert.Contains("(-20…+20)", tableChunk.Text, StringComparison.Ordinal);
        Assert.Contains("мА", tableChunk.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void MultiEvidenceSelection_RetainsComplementaryChunks_WhenBudgetPermits()
    {
        var meta = new DocumentMetadata("doc-1", "", "", "", "", "Ref", 0, "", DateOnly.MinValue, "Doc");

        var chunkA = new DocumentChunk("doc-1#1", "doc-1", 1, "Модуль MDS AI-8UI имеет 8 независимых каналов измерения аналоговых сигналов.", 0, 80, meta);
        var chunkB = new DocumentChunk("doc-1#2", "doc-1", 2, "[Контекст: MDS AI-8UI — Таблица 2.1] Диапазоны измерения тока: (-20…+20) мА, (0…20) мА, (4…20) мА.", 0, 100, meta);
        var distractor = new DocumentChunk("doc-1#3", "doc-1", 3, "Габаритные размеры модуля MDS AI-8UI составляют 22.5 x 100 x 115 мм.", 0, 70, meta);

        var candidates = new List<RetrievalResult>
        {
            new(chunkA, 0.95),
            new(chunkB, 0.90),
            new(distractor, 0.70)
        };

        var builder = new ContextBuilder(new TokenCounter());
        var budget = new ContextBudget(MaxTotalTokens: 8192, ReservedOutputTokens: 1024);

        var ragContext = builder.BuildContext(candidates, budget.ComputeAvailableContextTokens(500));

        Assert.True(ragContext.Sources.Count >= 2);
        Assert.Contains(ragContext.Sources, s => s.Chunk.ChunkId == "doc-1#1");
        Assert.Contains(ragContext.Sources, s => s.Chunk.ChunkId == "doc-1#2");
        Assert.Contains("[S1]", ragContext.FormattedContext, StringComparison.Ordinal);
        Assert.Contains("[S2]", ragContext.FormattedContext, StringComparison.Ordinal);
    }

    [Fact]
    public void TableCategoryAndUnitFidelity_PromptContractExplicitlyInstructsRowUnitPreservation()
    {
        var prompt = RagPromptBuilder.BuildPrompt("Какие диапазоны измерения тока?", RagContext.Empty);

        Assert.Contains("В таблицах строго сохраняй привязку значений к строкам и единицам измерения", prompt, StringComparison.Ordinal);
        Assert.Contains("не переноси значения между категориями (напряжение и ток)", prompt, StringComparison.Ordinal);
        Assert.Contains("выбирай только запрошенные строки", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InternalMarkerLeakage_CannotBeReturnedAsSuccessfulAnswer()
    {
        var meta = new DocumentMetadata("doc-1", "", "", "", "", "Ref", 0, "", DateOnly.MinValue, "Doc");
        var chunk = new DocumentChunk("doc-1#1", "doc-1", 1, "Модули MDS имеют 8 каналов.", 0, 30, meta);

        var retriever = new FakeRetriever([new RetrievalResult(chunk, 0.95)]);
        var fakeChat = new FakeStaticChatClient("КОНЕЦ КОНТЕКСТА\nОтвет: 8 каналов [S1]");

        var pipeline = new RagPipeline(retriever, fakeChat);
        var response = await pipeline.AskAsync("Сколько каналов?");

        Assert.False(response.Diagnostics.RequiredCitationsSatisfied);
        Assert.True(response.Diagnostics.HasInternalMarkerLeakage);
        Assert.DoesNotContain("КОНЕЦ КОНТЕКСТА", response.Answer, StringComparison.Ordinal);
        Assert.Contains("Ответ отклонен", response.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CitationOmission_MarkedAsUnsatisfiedInDiagnostics()
    {
        var meta = new DocumentMetadata("doc-1", "", "", "", "", "Ref", 0, "", DateOnly.MinValue, "Doc");
        var chunk = new DocumentChunk("doc-1#1", "doc-1", 1, "Модули MDS имеют 8 каналов.", 0, 30, meta);

        var retriever = new FakeRetriever([new RetrievalResult(chunk, 0.95)]);

        // 1. Model answers factually from sources, but omits citations
        var clientNoCitation = new FakeStaticChatClient("Модули MDS имеют 8 каналов.");
        var pipeline1 = new RagPipeline(retriever, clientNoCitation);
        var response1 = await pipeline1.AskAsync("Сколько каналов?");

        Assert.Equal(0, response1.Diagnostics.ValidCitationCount);
        Assert.False(response1.Diagnostics.RequiredCitationsSatisfied);

        // 2. Model answers factually with valid citation
        var clientWithCitation = new FakeStaticChatClient("Модули MDS имеют 8 каналов [S1].");
        var pipeline2 = new RagPipeline(retriever, clientWithCitation);
        var response2 = await pipeline2.AskAsync("Сколько каналов?");

        Assert.Equal(1, response2.Diagnostics.ValidCitationCount);
        Assert.True(response2.Diagnostics.RequiredCitationsSatisfied);

        // 3. Legitimate insufficient context answer without citations => satisfied
        var clientInsufficient = new FakeStaticChatClient("В предоставленном контексте недостаточно информации.");
        var pipeline3 = new RagPipeline(retriever, clientInsufficient);
        var response3 = await pipeline3.AskAsync("Сколько каналов?");

        Assert.True(response3.Diagnostics.RequiredCitationsSatisfied);
    }

    [Fact]
    public void TechnicalTokenNormalization_RepairsHyphenSplitsWithoutCorruptingProse()
    {
        var inputWithSplits = "Интерфейс RS- 485 и модель AI- 8UI/D работают по протоколу MODBUS RTU на RS- 485.";
        var repaired = DehyphenationRule.RepairDehyphenation(inputWithSplits);

        Assert.Equal("Интерфейс RS-485 и модель AI-8UI/D работают по протоколу MODBUS RTU на RS-485.", repaired);

        // Normal Russian hyphenated prose should NOT be altered
        var normalProse = "какой-то прибор по-прежнему работает в диапазоне 10 - 20 В";
        var unchangedProse = DehyphenationRule.RepairDehyphenation(normalProse);

        Assert.Equal(normalProse, unchangedProse);
    }

    private sealed class FakeRetriever : IRetriever
    {
        private readonly IReadOnlyList<RetrievalResult> _results;

        public FakeRetriever(IReadOnlyList<RetrievalResult> results)
        {
            _results = results;
        }

        public string Strategy => "fake";

        public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(RetrievalRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_results);
        }

        public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(string query, int topK, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_results);
        }
    }

    private sealed class FakeStaticChatClient : IChatClient
    {
        private readonly string _response;

        public FakeStaticChatClient(string response)
        {
            _response = response;
        }

        public ChatClientMetadata Metadata { get; } = new("fake");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _response)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, _response);
            await Task.CompletedTask;
        }

        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
