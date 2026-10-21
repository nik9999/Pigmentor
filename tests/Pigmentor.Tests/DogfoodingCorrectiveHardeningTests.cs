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
    public void TableEnumerationPromptContract_ExplicitlyInstructsExhaustiveCompletenessAndNoCategoryLeakage()
    {
        var normalPrompt = RagPromptBuilder.BuildPrompt("Перечисли только диапазоны измерения тока из таблицы 2.1.", RagContext.Empty);
        Assert.Contains("Если вопрос требует перечислить значения, диапазоны или строки таблицы", normalPrompt, StringComparison.Ordinal);
        Assert.Contains("включи все строки запрошенной категории полностью", normalPrompt, StringComparison.Ordinal);
        Assert.Contains("не останавливайся после первого совпадения", normalPrompt, StringComparison.Ordinal);
        Assert.Contains("не включай строки других категорий", normalPrompt, StringComparison.Ordinal);

        var retryPrompt = RagPromptBuilder.BuildRetryPrompt("Перечисли только диапазоны измерения тока из таблицы 2.1.", RagContext.Empty, "предыдущий ответ");
        Assert.Contains("Если вопрос требует перечислить значения или строки таблицы", retryPrompt, StringComparison.Ordinal);
        Assert.Contains("ВСЕ строки запрошенной категории полностью", retryPrompt, StringComparison.Ordinal);
        Assert.Contains("не включай другие категории", retryPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceValidator_AcceptsDecimalPercentagesAndUnits_WithCommaOrDot()
    {
        var meta = new DocumentMetadata("doc-pct", Silo: "technical-docs", "", "", "", "Manual", 2026, "", DateOnly.MinValue, "MDS AI-8UI");
        var chunk = new DocumentChunk(
            "doc-pct#1",
            "doc-pct",
            0,
            "Основная погрешность составляет не более 0,1 % во всем диапазоне. Напряжение 0,5 В.",
            0,
            100,
            meta);
        var citation = new Citation(1, "doc-pct", "doc-pct#1", "MDS AI-8UI", chunk.Text, meta);

        // Grounded with comma
        Assert.True(EvidenceValidator.ValidateEvidence("Основная погрешность: 0,1 % [S1], напряжение 0,5 В [S1].", [citation]));
        // Grounded with dot
        Assert.True(EvidenceValidator.ValidateEvidence("Основная погрешность: 0.1 % [S1], напряжение 0.5 В [S1].", [citation]));
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

    [Fact]
    public void TableRowBinding_DifferentRowValue_IsRejected()
    {
        var meta = new DocumentMetadata("synth1", Silo: "catalog", "", "", "", "Catalog", 2026, "", DateOnly.MinValue, "SynthDoc");
        var snippet = """
            | Артикул | Параметр | Значение |
            | --- | --- | --- |
            | A-100 | Тест | 100 |
            | B-200 | Тест | 200 |
            """;
        var citation = new Citation(1, "synth1", "synth1#1", "Catalog", snippet, meta);

        var invalidAnswer = "Модель A-100 — 200 [S1].";
        Assert.False(EvidenceValidator.ValidateEvidence(invalidAnswer, [citation]));
    }

    [Fact]
    public void TableRowBinding_SameRowValue_IsAccepted()
    {
        var meta = new DocumentMetadata("synth1", Silo: "catalog", "", "", "", "Catalog", 2026, "", DateOnly.MinValue, "SynthDoc");
        var snippet = """
            | Артикул | Параметр | Значение |
            | --- | --- | --- |
            | A-100 | Тест | 100 |
            | B-200 | Тест | 200 |
            """;
        var citation = new Citation(1, "synth1", "synth1#1", "Catalog", snippet, meta);

        var validAnswer = "Модель A-100 — 100 [S1].";
        Assert.True(EvidenceValidator.ValidateEvidence(validAnswer, [citation]));
    }

    [Fact]
    public void TableRowBinding_SameSourceDifferentRowContamination_Fails()
    {
        var meta = new DocumentMetadata("single-src", Silo: "catalog", "", "", "", "Catalog", 2026, "", DateOnly.MinValue, "Doc");
        var snippet = """
            Таблица данных:
            | Модель | Цена |
            | --- | --- |
            | A-100 | 100 |
            | B-200 | 200 |
            """;
        var citation = new Citation(1, "single-src", "single-src#1", "Doc", snippet, meta);

        var contaminatedAnswer = "Стоимость датчика A-100 составляет 200 руб. [S1]";
        var (isValid, reason) = EvidenceValidator.ValidateEvidenceWithReason(contaminatedAnswer, [citation]);

        Assert.False(isValid);
        Assert.Contains("row-binding violation", reason);
    }

    [Fact]
    public void TableExtremum_FilterPlusMax_AcceptsOptimalAndRejectsSuboptimal()
    {
        var meta = new DocumentMetadata("ext-table", Silo: "protocols", "", "", "", "Protocols", 2026, "", DateOnly.MinValue, "Protocols");
        var snippet = """
            | Устройство | Протокол | Параметр |
            | --- | --- | --- |
            | A | protocol X | 12 |
            | B | protocol Y | 50 |
            | C | protocol X | 30 |
            """;
        var citation = new Citation(1, "ext-table", "ext-table#1", "Protocols", snippet, meta);

        var question = "Какой объект с протоколом protocol X имеет максимальное значение?";

        var optimalAnswer = "Объект C с протоколом protocol X имеет максимальное значение 30 [S1].";
        var (isOptValid, optReason) = EvidenceValidator.ValidateEvidenceWithReason(optimalAnswer, [citation], question);
        Assert.True(isOptValid, optReason);

        var suboptimalAnswer = "Объект A с протоколом protocol X имеет максимальное значение 12 [S1].";
        var (isValid, reason) = EvidenceValidator.ValidateEvidenceWithReason(suboptimalAnswer, [citation], question);
        Assert.False(isValid);
        Assert.Contains("extremum violation", reason);
    }

    [Fact]
    public void TableArithmetic_BasePriceFromWrongRow_IsRejectedBeforeDerivedAcceptance()
    {
        var meta = new DocumentMetadata("prices", Silo: "catalog", "", "", "", "Catalog", 2026, "", DateOnly.MinValue, "Catalog");
        var snippet = """
            | Модель | Цена, руб. |
            | --- | --- |
            | A-100 | 100 |
            | B-200 | 200 |
            """;
        var citation = new Citation(1, "prices", "prices#1", "Catalog", snippet, meta);

        var question = "Если заказать 5 датчиков A-100, какая будет стоимость?";

        var wrongBaseAnswer = "Стоимость 5 датчиков A-100: 200 руб. × 5 = 1000 руб. [S1]";
        var (isWrongValid, wrongReason) = EvidenceValidator.ValidateEvidenceWithReason(wrongBaseAnswer, [citation], question);
        Assert.False(isWrongValid);
        Assert.Contains("row-binding violation", wrongReason);

        var correctBaseAnswer = "Стоимость 5 датчиков A-100: 100 руб. × 5 = 500 руб. [S1]";
        var (isCorrectValid, _) = EvidenceValidator.ValidateEvidenceWithReason(correctBaseAnswer, [citation], question);
        Assert.True(isCorrectValid);
    }

    [Fact]
    public void TableRowBinding_ImplicitEntityInAnswer_ResolvedFromQuestionAndValidated()
    {
        var meta = new DocumentMetadata("prices", Silo: "catalog", "", "", "", "Catalog", 2026, "", DateOnly.MinValue, "Catalog");
        var snippet = """
            | Модель | Цена, руб. |
            | --- | --- |
            | A-100 | 100 |
            | B-200 | 200 |
            """;
        var citation = new Citation(1, "prices", "prices#1", "Catalog", snippet, meta);

        var question = "Если заказать 5 датчиков A-100, какая будет стоимость?";

        // Model omits "A-100" in its answer text and picks 200 from B-200
        var implicitWrongAnswer = "Стоимость до скидки: 200 руб. × 5 = 1000 руб. [S1]";
        var (isWrongValid, wrongReason) = EvidenceValidator.ValidateEvidenceWithReason(implicitWrongAnswer, [citation], question);
        Assert.False(isWrongValid);
        Assert.Contains("row-binding violation", wrongReason);

        // Model omits "A-100" but picks correct 100 from A-100
        var implicitCorrectAnswer = "Стоимость до скидки: 100 руб. × 5 = 500 руб. [S1]";
        var (isCorrectValid, reason) = EvidenceValidator.ValidateEvidenceWithReason(implicitCorrectAnswer, [citation], question);
        Assert.True(isCorrectValid, reason);
    }

    [Fact]
    public void TestA_TargetNumericColumn_IsNotLast_SelectedCorrectly()
    {
        var meta = new DocumentMetadata("tab-a", Silo: "catalog", "", "", "", "Catalog", 2026, "", DateOnly.MinValue, "Catalog");
        var snippet = """
            | Модель | Гарантия, мес. | Цена |
            | --- | --- | --- |
            | A-100 | 36 | 100 |
            | B-200 | 24 | 500 |
            """;
        var citation = new Citation(1, "tab-a", "tab-a#1", "Catalog", snippet, meta);
        var question = "Какая модель имеет максимальный гарантийный срок?";

        var correct = "Модель A-100 имеет максимальный гарантийный срок 36 месяцев [S1].";
        Assert.True(EvidenceValidator.ValidateEvidence(correct, [citation], question));

        var suboptimal = "Модель B-200 имеет максимальный гарантийный срок 24 месяца [S1].";
        var (isValid, reason) = EvidenceValidator.ValidateEvidenceWithReason(suboptimal, [citation], question);
        Assert.False(isValid);
        Assert.Contains("extremum violation", reason);
    }

    [Fact]
    public void TestB_GenericArbitraryColumn_PowerNotYear()
    {
        var meta = new DocumentMetadata("tab-b", Silo: "specs", "", "", "", "Specs", 2026, "", DateOnly.MinValue, "Specs");
        var snippet = """
            | Устройство | Мощность, Вт | Год |
            | --- | --- | --- |
            | X-100 | 250 | 2024 |
            | Y-200 | 400 | 2020 |
            """;
        var citation = new Citation(1, "tab-b", "tab-b#1", "Specs", snippet, meta);
        var question = "Какое устройство имеет наибольшую мощность?";

        var correct = "Устройство Y-200 имеет наибольшую мощность 400 Вт [S1].";
        Assert.True(EvidenceValidator.ValidateEvidence(correct, [citation], question));

        var suboptimal = "Устройство X-100 имеет наибольшую мощность 250 Вт [S1].";
        var (isValid, reason) = EvidenceValidator.ValidateEvidenceWithReason(suboptimal, [citation], question);
        Assert.False(isValid);
        Assert.Contains("extremum violation", reason);
    }

    [Fact]
    public void TestC_MinimumOperation_SelectsSmallest()
    {
        var meta = new DocumentMetadata("tab-c", Silo: "specs", "", "", "", "Specs", 2026, "", DateOnly.MinValue, "Specs");
        var snippet = """
            | Прибор | Масса, кг | Цена |
            | --- | --- | --- |
            | X-100 | 8 | 100 |
            | Y-200 | 5 | 500 |
            """;
        var citation = new Citation(1, "tab-c", "tab-c#1", "Specs", snippet, meta);
        var question = "Какой прибор имеет минимальную массу?";

        var correct = "Прибор Y-200 имеет минимальную массу 5 кг [S1].";
        Assert.True(EvidenceValidator.ValidateEvidence(correct, [citation], question));

        var suboptimal = "Прибор X-100 имеет минимальную массу 8 кг [S1].";
        var (isValid, reason) = EvidenceValidator.ValidateEvidenceWithReason(suboptimal, [citation], question);
        Assert.False(isValid);
        Assert.Contains("extremum violation", reason);
    }

    [Fact]
    public void TestD_FilterPlusComparison_ExcludesMismatchedAndSelectsTarget()
    {
        var meta = new DocumentMetadata("tab-d", Silo: "specs", "", "", "", "Specs", 2026, "", DateOnly.MinValue, "Specs");
        var snippet = """
            | Модель | Протокол | Ресурс, ч | Цена |
            | --- | --- | --- | --- |
            | A-100 | X | 1000 | 900 |
            | B-200 | Y | 5000 | 800 |
            | C-300 | X | 3000 | 700 |
            """;
        var citation = new Citation(1, "tab-d", "tab-d#1", "Specs", snippet, meta);
        var question = "Какая модель с протоколом X имеет максимальный ресурс?";

        var correct = "Модель C-300 с протоколом X имеет максимальный ресурс 3000 ч [S1].";
        var (isCorValid, corReason) = EvidenceValidator.ValidateEvidenceWithReason(correct, [citation], question);
        Assert.True(isCorValid, corReason);

        var suboptimal = "Модель A-100 с протоколом X имеет максимальный ресурс 1000 ч [S1].";
        var (isValid, reason) = EvidenceValidator.ValidateEvidenceWithReason(suboptimal, [citation], question);
        Assert.False(isValid);
        Assert.Contains("extremum violation", reason);
    }

    [Fact]
    public void TestE_UnresolvedAmbiguity_RefrainsFromArbitraryChoice()
    {
        var meta = new DocumentMetadata("tab-e", Silo: "specs", "", "", "", "Specs", 2026, "", DateOnly.MinValue, "Specs");
        var snippet = """
            | Модель | Параметр A | Параметр B |
            | --- | --- | --- |
            | A-100 | 10 | 100 |
            | B-200 | 20 | 50 |
            """;
        var citation = new Citation(1, "tab-e", "tab-e#1", "Specs", snippet, meta);
        var question = "Какая модель имеет максимальное значение?";

        var answer = "Модель B-200 имеет максимальное значение [S1].";
        // Ambiguity policy: does NOT make an arbitrary guess, does NOT falsely reject
        var (isValid, reason) = EvidenceValidator.ValidateEvidenceWithReason(answer, [citation], question);
        Assert.True(isValid);
        Assert.Null(reason);
    }

    [Fact]
    public void TestF_CitationFidelityGeneric_RejectsIrrelevantTableAndAcceptsRelevant()
    {
        var meta1 = new DocumentMetadata("tab-f1", Silo: "specs", "", "", "", "Specs", 2026, "", DateOnly.MinValue, "Specs");
        var snippet1 = """
            | Устройство | Год |
            | --- | --- |
            | X-100 | 2024 |
            | Y-200 | 2020 |
            """;
        var citation1 = new Citation(1, "tab-f1", "tab-f1#1", "Specs", snippet1, meta1);

        var meta2 = new DocumentMetadata("tab-f2", Silo: "specs", "", "", "", "Specs", 2026, "", DateOnly.MinValue, "Specs");
        var snippet2 = """
            | Устройство | Мощность, Вт |
            | --- | --- |
            | X-100 | 250 |
            | Y-200 | 400 |
            """;
        var citation2 = new Citation(2, "tab-f2", "tab-f2#1", "Specs", snippet2, meta2);

        var question = "Какое устройство имеет максимальную мощность?";

        // If only citation 1 (with year) is cited: fails citation fidelity
        var answerCitingOnlyYear = "Устройство Y-200 имеет максимальную мощность 400 Вт [S1].";
        var (isCit1Valid, cit1Reason) = EvidenceValidator.ValidateEvidenceWithReason(answerCitingOnlyYear, [citation1], question);
        Assert.False(isCit1Valid);
        Assert.Contains("характеристиками, запрошенными для сравнения", cit1Reason);

        // If citation 2 (with power) is cited: passes citation fidelity
        var answerCitingPower = "Устройство Y-200 имеет максимальную мощность 400 Вт [S2].";
        var (isCit2Valid, _) = EvidenceValidator.ValidateEvidenceWithReason(answerCitingPower, [citation2], question);
        Assert.True(isCit2Valid);
    }

    [Fact]
    public void CalculationSourceRow_MissingModelRow_OnlyDiscountCited_FailsValidation()
    {
        var meta = new DocumentMetadata("doc-cond", Silo: "catalog", "", "", "", "Conditions", 2026, "", DateOnly.MinValue, "Conditions");
        var discountSnippet = """
            | Условие | Скидка |
            | --- | --- |
            | Скидка от 5 единиц одной модели | 7 % |
            | Скидка от 10 единиц одной модели | 10 % |
            """;
        var citation2 = new Citation(2, "doc-cond", "doc-cond#1", "Conditions", discountSnippet, meta);

        var question = "Если заказать 5 датчиков TM-200, какая будет стоимость до скидки и после применения указанной в документе скидки?";
        var answer = "Стоимость до скидки: 22100 руб. Стоимость после скидки (7%): 12838 руб. (22100 * 0,93) [S2]";

        var (isValid, reason) = EvidenceValidator.ValidateEvidenceWithReason(answer, [citation2], question);

        Assert.False(isValid);
        Assert.NotNull(reason);
        Assert.Contains("Цитируемые источники не содержат строку таблицы для запрошенной модели 'TM-200' с исходными данными для расчёта", reason);
    }

    [Fact]
    public void CalculationSourceRow_RequestedModelRowPresent_PassesSourceRowValidation()
    {
        var metaPrices = new DocumentMetadata("doc-prices", Silo: "catalog", "", "", "", "Prices", 2026, "", DateOnly.MinValue, "Prices");
        var priceSnippet = """
            | Модель | Цена, руб. |
            | --- | --- |
            | TM-200 | 19600 |
            | PR-25 | 22100 |
            """;
        var citation1 = new Citation(1, "doc-prices", "doc-prices#1", "Prices", priceSnippet, metaPrices);

        var metaCond = new DocumentMetadata("doc-cond", Silo: "catalog", "", "", "", "Conditions", 2026, "", DateOnly.MinValue, "Conditions");
        var discountSnippet = """
            | Условие | Скидка |
            | --- | --- |
            | Скидка от 5 единиц одной модели | 7 % |
            """;
        var citation2 = new Citation(2, "doc-cond", "doc-cond#1", "Conditions", discountSnippet, metaCond);

        var question = "Если заказать 5 датчиков TM-200, какая будет стоимость до скидки и после применения указанной в документе скидки?";
        var answer = "Стоимость до скидки: 98000 руб. (19600 руб. за единицу) [S1]. Стоимость после скидки с учетом 7 % [S2] составит 91140 руб.";

        var (isValid, reason) = EvidenceValidator.ValidateEvidenceWithReason(answer, [citation1, citation2], question);

        Assert.True(isValid, reason);
        Assert.Null(reason);
    }

    [Fact]
    public void CalculationSourceRow_WrongModelRowOnly_FailsValidation()
    {
        var metaPrices = new DocumentMetadata("doc-prices", Silo: "catalog", "", "", "", "Prices", 2026, "", DateOnly.MinValue, "Prices");
        var priceSnippet = """
            | Модель | Цена, руб. |
            | --- | --- |
            | PR-25 | 22100 |
            """;
        var citation1 = new Citation(1, "doc-prices", "doc-prices#1", "Prices", priceSnippet, metaPrices);

        var metaCond = new DocumentMetadata("doc-cond", Silo: "catalog", "", "", "", "Conditions", 2026, "", DateOnly.MinValue, "Conditions");
        var discountSnippet = """
            | Условие | Скидка |
            | --- | --- |
            | Скидка от 5 единиц одной модели | 7 % |
            """;
        var citation2 = new Citation(2, "doc-cond", "doc-cond#1", "Conditions", discountSnippet, metaCond);

        var question = "Если заказать 5 датчиков TM-200, какая будет стоимость до скидки и после применения указанной в документе скидки?";
        var answer = "Стоимость до скидки: 22100 руб. [S1]. Скидка составляет 7 % [S2].";

        var (isValid, reason) = EvidenceValidator.ValidateEvidenceWithReason(answer, [citation1, citation2], question);

        Assert.False(isValid);
        Assert.NotNull(reason);
        Assert.Contains("Цитируемые источники не содержат строку таблицы для запрошенной модели 'TM-200' с исходными данными для расчёта", reason);
    }

    [Fact]
    public void CalculationSourceRow_GenericArtificialModel_PassesValidationOnlyWhenRowPresent()
    {
        var meta = new DocumentMetadata("doc-gen", Silo: "catalog", "", "", "", "Catalog", 2026, "", DateOnly.MinValue, "Catalog");
        var snippetWrong = """
            | Модель | Цена, руб. |
            | --- | --- |
            | AB-900 | 1250 |
            """;
        var citationWrong = new Citation(1, "doc-gen", "doc-gen#1", "Catalog", snippetWrong, meta);

        var snippetCorrect = """
            | Модель | Цена, руб. |
            | --- | --- |
            | ZX-410 | 1250 |
            """;
        var citationCorrect = new Citation(2, "doc-gen", "doc-gen#2", "Catalog", snippetCorrect, meta);

        var question = "Какова стоимость заказа 10 приборов ZX-410?";

        // Answer citing only AB-900 table -> fails
        var answerWrong = "Стоимость заказа ZX-410 составляет 12500 руб. [S1]";
        var (isWrongValid, wrongReason) = EvidenceValidator.ValidateEvidenceWithReason(answerWrong, [citationWrong], question);
        Assert.False(isWrongValid);
        Assert.NotNull(wrongReason);
        Assert.Contains("Цитируемые источники не содержат строку таблицы для запрошенной модели 'ZX-410' с исходными данными для расчёта", wrongReason);

        // Answer citing ZX-410 table -> passes
        var answerCorrect = "Стоимость заказа ZX-410 составляет 12500 руб. [S2]";
        var (isCorrectValid, correctReason) = EvidenceValidator.ValidateEvidenceWithReason(answerCorrect, [citationCorrect], question);
        Assert.True(isCorrectValid, correctReason);
        Assert.Null(correctReason);
    }

    [Fact]
    public void CalculationSubtotal_CorrectSubtotal_PassesValidation()
    {
        var meta = new DocumentMetadata("doc-sub1", Silo: "catalog", "", "", "", "Catalog", 2026, "", DateOnly.MinValue, "Catalog");
        var snippet = """
            | Модель | Цена, руб. | Остаток | Гарантия |
            | --- | --- | --- | --- |
            | TM-200 | 19600 | 6 | 24 |
            """;
        var citation = new Citation(1, "doc-sub1", "doc-sub1#1", "Catalog", snippet, meta);
        var question = "Если заказать 5 TM-200, какая стоимость до скидки?";
        var answer = "Стоимость до скидки: 98000 руб. [S1]";

        var (isValid, reason) = EvidenceValidator.ValidateEvidenceWithReason(answer, [citation], question);

        Assert.True(isValid, reason);
        Assert.Null(reason);
    }

    [Fact]
    public void CalculationSubtotal_WrongSubtotal_FailsValidationWithClearReason()
    {
        var meta = new DocumentMetadata("doc-sub2", Silo: "catalog", "", "", "", "Catalog", 2026, "", DateOnly.MinValue, "Catalog");
        var snippet = """
            | Модель | Цена, руб. | Остаток | Гарантия |
            | --- | --- | --- | --- |
            | TM-200 | 19600 | 6 | 24 |
            """;
        var citation = new Citation(1, "doc-sub2", "doc-sub2#1", "Catalog", snippet, meta);
        var question = "Если заказать 5 TM-200, какая стоимость до скидки?";
        var answer = "Стоимость до скидки: 97000 руб. [S1]";

        var (isValid, reason) = EvidenceValidator.ValidateEvidenceWithReason(answer, [citation], question);

        Assert.False(isValid);
        Assert.NotNull(reason);
        Assert.Contains("Заявленная стоимость до скидки 97000 не соответствует расчёту 19600 × 5 = 98000", reason);
    }

    [Fact]
    public void CalculationSubtotal_WrongRowPriceArithmetic_FailsValidation()
    {
        var meta = new DocumentMetadata("doc-sub3", Silo: "catalog", "", "", "", "Catalog", 2026, "", DateOnly.MinValue, "Catalog");
        var snippet = """
            | Модель | Цена, руб. |
            | --- | --- |
            | TM-200 | 19600 |
            | PR-25  | 22100 |
            """;
        var citation = new Citation(1, "doc-sub3", "doc-sub3#1", "Catalog", snippet, meta);
        var question = "Заказать 5 TM-200";
        var answer = "22100 × 5 = 110500 руб. [S1]";

        var (isValid, reason) = EvidenceValidator.ValidateEvidenceWithReason(answer, [citation], question);

        Assert.False(isValid);
        Assert.NotNull(reason);
    }

    [Fact]
    public void CalculationSubtotal_PriceColumnAmongOtherNumericColumns_SelectedCorrectly()
    {
        var meta = new DocumentMetadata("doc-sub4", Silo: "catalog", "", "", "", "Catalog", 2026, "", DateOnly.MinValue, "Catalog");
        var snippet = """
            | Модель | Остаток | Цена, руб. | Гарантия, мес. |
            | --- | --- | --- | --- |
            | ZX-410 | 12 | 1250 | 36 |
            """;
        var citation = new Citation(1, "doc-sub4", "doc-sub4#1", "Catalog", snippet, meta);
        var question = "Сколько стоят 3 ZX-410 до скидки?";
        var answer = "Стоимость до скидки: 3750 руб. [S1]";

        var (isValid, reason) = EvidenceValidator.ValidateEvidenceWithReason(answer, [citation], question);

        Assert.True(isValid, reason);
        Assert.Null(reason);
    }

    [Fact]
    public void CalculationSubtotal_NoPriceHeader_RefrainsFromArbitraryNumericFallback()
    {
        var meta = new DocumentMetadata("doc-sub5", Silo: "catalog", "", "", "", "Catalog", 2026, "", DateOnly.MinValue, "Catalog");
        var snippet = """
            | Модель | Остаток | Гарантия |
            | --- | --- | --- |
            | ZX-410 | 12 | 36 |
            """;
        var citation = new Citation(1, "doc-sub5", "doc-sub5#1", "Catalog", snippet, meta);
        var question = "Сколько стоят 3 ZX-410?";
        var answer = "По модели ZX-410 указаны остаток 12 и гарантия 36 [S1].";

        var (isValid, reason) = EvidenceValidator.ValidateEvidenceWithReason(answer, [citation], question);

        // Ambiguity policy: no price header -> unresolved, does not falsely reject based on 12*3 or 36*3
        Assert.True(isValid, reason);
        Assert.Null(reason);
    }

    [Fact]
    public void CalculationSubtotal_WrongOperandCorrectResult_FailsValidation()
    {
        var meta = new DocumentMetadata("doc-sub-op1", Silo: "catalog", "", "", "", "Catalog", 2026, "", DateOnly.MinValue, "Catalog");
        var snippet = """
            | Модель | Цена, руб. |
            | --- | --- |
            | TM-200 | 19600 |
            """;
        var citation = new Citation(1, "doc-sub-op1", "doc-sub-op1#1", "Catalog", snippet, meta);
        var question = "Заказать 5 TM-200";
        var answer = "12345 × 5 = 98000 руб. [S1]";

        var (isValid, reason) = EvidenceValidator.ValidateEvidenceWithReason(answer, [citation], question);

        Assert.False(isValid);
        Assert.NotNull(reason);
        Assert.Contains("В расчёте использована цена 12345, но для модели 'TM-200' в таблице указана цена 19600", reason);
    }

    [Fact]
    public void CalculationSubtotal_ReversedCorrectOperands_PassesValidation()
    {
        var meta = new DocumentMetadata("doc-sub-op2", Silo: "catalog", "", "", "", "Catalog", 2026, "", DateOnly.MinValue, "Catalog");
        var snippet = """
            | Модель | Цена, руб. |
            | --- | --- |
            | TM-200 | 19600 |
            """;
        var citation = new Citation(1, "doc-sub-op2", "doc-sub-op2#1", "Catalog", snippet, meta);
        var question = "Заказать 5 TM-200";
        var answer = "5 × 19600 = 98000 руб. [S1]";

        var (isValid, reason) = EvidenceValidator.ValidateEvidenceWithReason(answer, [citation], question);

        Assert.True(isValid, reason);
        Assert.Null(reason);
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
