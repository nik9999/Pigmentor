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

    [Theory]
    [InlineData("MOD- BUS RTU", "MODBUS RTU")]
    [InlineData("RS- 485", "RS-485")]
    [InlineData("AI- 8UI/D", "AI-8UI/D")]
    [InlineData("обслуживаю-\nщего", "обслуживающего")]
    [InlineData("обслуживаю- щего", "обслуживающего")]
    [InlineData("незави-\r\nсимых", "независимых")]
    [InlineData("незави- симых", "независимых")]
    public void TechnicalTokenNormalization_DehyphenatesCorrectly(string input, string expected)
    {
        var repaired = DehyphenationRule.RepairDehyphenation(input);
        Assert.Equal(expected, repaired);
    }

    [Theory]
    [InlineData("RS-485")]
    [InlineData("AI-8UI/D")]
    [InlineData("-20…+20")]
    [InlineData("0…20")]
    [InlineData("4-20 mA")]
    [InlineData("-100…+300 °C")]
    [InlineData("PX-400")]
    [InlineData("клиент-сервер")]
    public void TechnicalTokenNormalization_PreservesValidTechnicalTokens(string token)
    {
        var text = $"Прибор {token} эксплуатируется в штатном режиме.";
        var repaired = DehyphenationRule.RepairDehyphenation(text);
        Assert.Contains(token, repaired, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Какие товары из каталога поддерживают RS-485?")]
    [InlineData("Какой товар в каталоге имеет максимальную стоимость?")]
    [InlineData("Выведи по каталогу цены на оборудование")]
    [InlineData("Какой товар имеет наибольший гарантийный срок?")]
    [InlineData("Сколько единиц оборудования есть на складе?")]
    public void QueryScopeResolver_ResolvesProductCatalogScope_ForExplicitCatalogQueries(string query)
    {
        var filter = QueryScopeResolver.ResolveScope(query);
        Assert.NotNull(filter);

        var catalogMeta = new DocumentMetadata("cat1", Silo: "product-catalog", "", "", "", "Sheet", 2026, "", DateOnly.MinValue, "Catalog");
        var docsMeta = new DocumentMetadata("doc1", Silo: "technical-docs", "", "", "", "Manual", 2026, "", DateOnly.MinValue, "Doc");

        Assert.True(filter.Matches(catalogMeta));
        Assert.False(filter.Matches(docsMeta));
    }

    [Theory]
    [InlineData("Что поддерживает Modbus RTU?")]
    [InlineData("Для чего предназначены модули MDS AI-8UI?")]
    [InlineData("Какая рабочая температура эксплуатации прибора?")]
    public void QueryScopeResolver_ReturnsNull_ForGenericQueries(string query)
    {
        var filter = QueryScopeResolver.ResolveScope(query);
        Assert.Null(filter);
    }

    [Fact]
    public void EvidenceValidator_ValidatesModelIdentifierAndUnitValues()
    {
        var meta = new DocumentMetadata("doc1", "", "", "", "", "Manual", 2026, "", DateOnly.MinValue, "Manual");
        var chunk = new DocumentChunk("doc1#1", "doc1", 0, "Контроллер TM-200 питается от напряжения 24 В и работает при температуре до 55 °C. Гарантия 36 месяцев. Скидка 7 %.", 0, 100, meta);
        var citation = new Citation(1, "doc1", "doc1#1", "Manual", chunk.Text, meta);

        // Grounded answer: TM-200, 24 В, 55 °C, 36 месяцев, 7 %
        var validAnswer = "Контроллер TM-200 [S1] требует питание 24 В [S1], предел 55 °C [S1], гарантия 36 месяцев [S1], скидка 7 % [S1].";
        Assert.True(EvidenceValidator.ValidateEvidence(validAnswer, [citation]));

        // Ungrounded model: PX-400 not in chunk
        var invalidModelAnswer = "Контроллер PX-400 [S1] требует питание 24 В [S1].";
        Assert.False(EvidenceValidator.ValidateEvidence(invalidModelAnswer, [citation]));

        // Ungrounded unit value: 70 °C instead of 55 °C
        var invalidUnitAnswer = "Контроллер TM-200 [S1] работает при температуре до 70 °C [S1].";
        Assert.False(EvidenceValidator.ValidateEvidence(invalidUnitAnswer, [citation]));

        // Ungrounded percentage: 10 % instead of 7 %
        var invalidPercentAnswer = "Контроллер TM-200 [S1] имеет скидку 10 % [S1].";
        Assert.False(EvidenceValidator.ValidateEvidence(invalidPercentAnswer, [citation]));
    }

    [Fact]
    public void EvidenceValidator_DocxTableAttribution_RequiresExactMaintenanceTableEvidence()
    {
        var meta = new DocumentMetadata("docx1", Silo: "technical-docs", "", "", "", "Regulation", 2026, "", DateOnly.MinValue, "PX-400");
        var table1Chunk = new DocumentChunk("docx1#t1", "docx1", 0, "Таблица 1 Проверка питания: напряжение питания 24 В ±10 % постоянного тока.", 0, 100, meta);
        var sec3Chunk = new DocumentChunk("docx1#s3", "docx1", 1, "Раздел 3 Аварийные ситуации: допустимый диапазон 20…28 В.", 0, 100, meta);

        var table1Citation = new Citation(1, "docx1", "docx1#t1", "PX-400", table1Chunk.Text, meta);
        var sec3Citation = new Citation(2, "docx1", "docx1#s3", "PX-400", sec3Chunk.Text, meta);

        // Claiming 24 В citing Table 1 succeeds
        var answerTable1 = "Номинальное напряжение питания составляет 24 В [S1].";
        Assert.True(EvidenceValidator.ValidateEvidence(answerTable1, [table1Citation]));

        // Claiming 24 В citing only Section 3 fails because Section 3 contains only 20…28 В
        Assert.False(EvidenceValidator.ValidateEvidence(answerTable1, [sec3Citation]));
    }

    [Fact]
    public void EvidenceValidator_CurrentRanges_RecognizesSemanticallyIdenticalRepresentations_Passes()
    {
        var meta = new DocumentMetadata("doc-table", Silo: "technical-docs", "", "", "", "Manual", 2026, "", DateOnly.MinValue, "MDS AI-8UI");
        var chunk = new DocumentChunk(
            "doc-table#1",
            "doc-table",
            0,
            "Таблица 2.1 | Ток | (-20…+20) | мА | ±40 мкА | | Ток | (0…20) | мА | ±20 мкА | | Ток | (4…20) | мА | ±16 мкА |",
            0,
            120,
            meta);
        var citation = new Citation(1, "doc-table", "doc-table#1", "MDS AI-8UI", chunk.Text, meta);

        // 1. Parenthesized ranges
        var answerWithParens = "Диапазоны измерения тока: (-20…+20) мА, (0…20) мА, (4…20) мА [S1].";
        Assert.True(EvidenceValidator.ValidateEvidence(answerWithParens, [citation]));

        // 2. Ranges without parentheses
        var answerWithoutParens = "Диапазоны измерения тока: -20…+20 мА, 0…20 мА, 4…20 мА [S1].";
        Assert.True(EvidenceValidator.ValidateEvidence(answerWithoutParens, [citation]));

        // 3. Dot ellipsis formatting
        var answerWithDots = "Диапазоны измерения тока: (-20...+20) мА, (0...20) мА, (4...20) мА [S1].";
        Assert.True(EvidenceValidator.ValidateEvidence(answerWithDots, [citation]));

        // 4. Hyphenated 4-20 mA
        var answerHyphen = "Диапазон тока составляет 4-20 мА [S1].";
        Assert.True(EvidenceValidator.ValidateEvidence(answerHyphen, [citation]));

        // 5. Negative test: ungrounded range
        var invalidRangeAnswer = "Диапазон тока составляет 10…20 мА [S1].";
        Assert.False(EvidenceValidator.ValidateEvidence(invalidRangeAnswer, [citation]));
    }

    [Fact]
    public void EvidenceValidator_VoltageTolerance_RecognizesMaintenanceAndRejectsEmergencyLimits()
    {
        var meta = new DocumentMetadata("docx1", Silo: "technical-docs", "", "", "", "Regulation", 2026, "", DateOnly.MinValue, "PX-400");
        var maintenanceChunk = new DocumentChunk(
            "docx1#maint",
            "docx1",
            0,
            "Таблица 1 Проверка питания: напряжение питания 24 В ±10 % постоянного тока.",
            0,
            100,
            meta);
        var emergencyChunk = new DocumentChunk(
            "docx1#emerg",
            "docx1",
            1,
            "Раздел 3 Аварийные условия: отключение питания при напряжении ниже 20 В или выше 28 В.",
            0,
            100,
            meta);

        var maintCitation = new Citation(1, "docx1", "docx1#maint", "PX-400", maintenanceChunk.Text, meta);
        var emergCitation = new Citation(2, "docx1", "docx1#emerg", "PX-400", emergencyChunk.Text, meta);

        // Grounded tolerance with formatting variants citing Table 1
        Assert.True(EvidenceValidator.ValidateEvidence("Напряжение питания: 24 В ±10% [S1].", [maintCitation]));
        Assert.True(EvidenceValidator.ValidateEvidence("Напряжение питания: 24 В ± 10 % [S1].", [maintCitation]));
        Assert.True(EvidenceValidator.ValidateEvidence("Напряжение питания: 24В ±10% [S1].", [maintCitation]));
        Assert.True(EvidenceValidator.ValidateEvidence("Напряжение питания: 24 В ±10 % [S1].", [maintCitation]));

        // Negative test: answer claims 24 В ±10% citing ONLY emergency limits (20 В / 28 В) -> must fail
        Assert.False(EvidenceValidator.ValidateEvidence("Напряжение питания: 24 В ±10% [S2].", [emergCitation]));

        // Negative test: answer claims incorrect tolerance 24 В ±20% citing Table 1 -> must fail
        Assert.False(EvidenceValidator.ValidateEvidence("Напряжение питания: 24 В ±20% [S1].", [maintCitation]));
    }

    [Fact]
    public void EvidenceValidator_TableCategoryFidelity_PreventsVoltageInCurrentCategoryAndViceVersa()
    {
        var meta = new DocumentMetadata("doc-table", Silo: "technical-docs", "", "", "", "Manual", 2026, "", DateOnly.MinValue, "MDS AI-8UI");
        var chunk = new DocumentChunk(
            "doc-table#1",
            "doc-table",
            0,
            "Таблица 2.1 | Напряжение | (-150…+150) | мВ | | Напряжение | (0…10) | В | | Ток | (-20…+20) | мА | | Ток | (4…20) | мА |",
            0,
            120,
            meta);
        var citation = new Citation(1, "doc-table", "doc-table#1", "MDS AI-8UI", chunk.Text, meta);

        // Misclassified: voltage range (0…10) В placed under "Ток:" category
        var misclassifiedAnswer =
            "Напряжение:\n" +
            "- (-150…+150) мВ [S1]\n\n" +
            "Ток:\n" +
            "- (-20…+20) мА [S1]\n" +
            "- (0…10) В [S1]\n" +
            "- (4…20) мА [S1]";
        Assert.False(EvidenceValidator.ValidateEvidence(misclassifiedAnswer, [citation]));

        // Correctly classified: voltage under Напряжение, current under Ток
        var correctlyClassifiedAnswer =
            "Напряжение:\n" +
            "- (-150…+150) мВ [S1]\n" +
            "- (0…10) В [S1]\n\n" +
            "Ток:\n" +
            "- (-20…+20) мА [S1]\n" +
            "- (4…20) мА [S1]";
        Assert.True(EvidenceValidator.ValidateEvidence(correctlyClassifiedAnswer, [citation]));
    }

    [Fact]
    public void EvidenceValidator_ProximateCitations_EnforcesClaimToCitationAttribution()
    {
        var meta = new DocumentMetadata("cat1", Silo: "product-catalog", "", "", "", "Catalog", 2026, "", DateOnly.MinValue, "Catalog");
        var catChunk = new DocumentChunk("cat1#1", "cat1", 0, "Датчик TM-200 стоит 19600 руб. Диапазон от -100 до +300 °C.", 0, 100, meta);
        var condChunk = new DocumentChunk("cat1#2", "cat1", 1, "Скидка от 5 единиц составляет 7 %.", 0, 100, meta);

        var s1 = new Citation(1, "cat1", "cat1#1", "Catalog", catChunk.Text, meta);
        var s2 = new Citation(2, "cat1", "cat1#2", "Conditions", condChunk.Text, meta);

        // Factual claim TM-200 / 19600 cited with S2 (Conditions, which does not contain 19600) -> must FAIL
        var wrongAttribution =
            "Стоимость датчика TM-200 составляет 19 600 руб. [S2]\n" +
            "Скидка от 5 единиц составляет 7 % [S2]";
        Assert.False(EvidenceValidator.ValidateEvidence(wrongAttribution, [s2]));

        // Correct attribution: price cited with S1, discount cited with S2 -> must PASS
        var correctAttribution =
            "Стоимость датчика TM-200 составляет 19 600 руб. [S1]\n" +
            "Скидка от 5 единиц составляет 7 % [S2]";
        Assert.True(EvidenceValidator.ValidateEvidence(correctAttribution, [s1, s2]));
    }

    [Fact]
    public async Task BoundedRetry_RetriesAtMostOnce_AndFailsSafelyIfStillUnsatisfied()
    {
        var meta = new DocumentMetadata("doc1", "", "", "", "", "Manual", 2026, "", DateOnly.MinValue, "Doc");
        var chunk = new DocumentChunk("doc1#1", "doc1", 0, "Параметры MDS AI-8UI: 8 каналов.", 0, 50, meta);
        var retriever = new FakeRetriever([new RetrievalResult(chunk, 1.0)]);

        // Always returns answers without citations
        var fakeChat = new CountingStaticChatClient("Устройство MDS AI-8UI имеет 8 каналов.");
        var pipeline = new RagPipeline(retriever, fakeChat);

        var response = await pipeline.AskAsync("Сколько каналов?");

        // 1 initial attempt + exactly 1 retry = 2 total LLM calls
        Assert.Equal(2, fakeChat.CallCount);
        Assert.False(response.Diagnostics.RequiredCitationsSatisfied);
        Assert.Equal("Не удалось подтвердить факты в ответе обязательными ссылками на источники контекста.", response.Answer);
    }

    [Fact]
    public async Task InsufficientContext_DoesNotTriggerRetry_OrCitationFailure()
    {
        var meta = new DocumentMetadata("doc1", "", "", "", "", "Manual", 2026, "", DateOnly.MinValue, "Doc");
        var chunk = new DocumentChunk("doc1#1", "doc1", 0, "Параметры MDS AI-8UI: 8 каналов.", 0, 50, meta);
        var retriever = new FakeRetriever([new RetrievalResult(chunk, 1.0)]);

        var fakeChat = new CountingStaticChatClient("В предоставленном контексте отсутствует информация о пароле Wi-Fi.");
        var pipeline = new RagPipeline(retriever, fakeChat);

        var response = await pipeline.AskAsync("Какой пароль от Wi-Fi сети?");

        // Only 1 call because insufficient context answer is valid without citations
        Assert.Equal(1, fakeChat.CallCount);
        Assert.True(response.Diagnostics.RequiredCitationsSatisfied);
        Assert.Contains("отсутствует информация", response.Answer, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CountingStaticChatClient : IChatClient
    {
        private readonly string _response;
        public int CallCount { get; private set; }

        public CountingStaticChatClient(string response)
        {
            _response = response;
        }

        public ChatClientMetadata Metadata { get; } = new("counting-fake");

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _response)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, _response);
            await Task.CompletedTask;
        }

        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
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
