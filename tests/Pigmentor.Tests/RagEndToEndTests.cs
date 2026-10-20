using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Generation;
using Pigmentor.Ingestion.Chunking;
using Pigmentor.Ingestion.Extraction;
using Pigmentor.Ingestion.Lifecycle;
using Pigmentor.Ingestion.Normalization;
using Pigmentor.Ingestion.Services;
using Pigmentor.Retrieval;
using Pigmentor.Tests.Fixtures;
using Pigmentor.VectorStore.SqliteVec;
using Xunit;

namespace Pigmentor.Tests;

public sealed class RagEndToEndTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _tempDbPath;
    private readonly string _connectionString;

    public RagEndToEndTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"pigmentor_e2e_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _tempDbPath = Path.Combine(_tempDirectory, "test_pigmentor.db");
        _connectionString = $"Data Source={_tempDbPath}";
    }

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
            // Best effort cleanup in temp directory
        }
    }

    [Fact]
    public async Task MultiFormat_Ingestion_To_Retrieval_And_Answer_SucceedsAcrossAllFormats()
    {
        // 1. Arrange Ingestion Infrastructure
        const int dimensions = 64;
        var embedder = new DeterministicSemanticEmbeddingService(dimensions);
        var chatClient = new DeterministicRagChatClient();

        var vecOptions = new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = dimensions,
            EmbeddingModel = embedder.EmbeddingModel
        };

        using var vectorStore = new SqliteVecStore(vecOptions);
        using var stateStore = new SqliteIngestionStateStore(_connectionString);

        var fakeOcr = new ExtractionAndNormalizationTests.FakeOcrEngine(
            defaultText: "Modbus RTU Register 40001 Temperature INT16",
            defaultConfidence: 0.98);

        var extractor = new DocumentExtractor(ocrEngine: fakeOcr);
        var normalizer = new DocumentNormalizer();
        var chunker = new StructuralDocumentChunker();

        var ingestionService = new DocumentIngestionService(
            stateStore: stateStore,
            vectorStore: vectorStore,
            chunker: chunker,
            embedder: embedder,
            documentExtractor: extractor,
            documentNormalizer: normalizer,
            logger: NullLogger<DocumentIngestionService>.Instance);

        // 2. Create Multi-Format Test Files
        var txtPath = MultiFormatFixtureFiles.CreateTxt(
            _tempDirectory,
            "power_spec.txt",
            "Для электропитания контроллера требуется стабилизированное напряжение 24 В постоянного тока.");

        var docxPath = MultiFormatFixtureFiles.CreateDocx(
            _tempDirectory,
            "comm_spec.docx",
            title: "Спецификация интерфейса связи",
            paragraphText: "Для интерфейса RS-485 установлена скорость передачи данных 115200 бит/с и согласующий резистор 120 Ω.");

        var xlsxPath = MultiFormatFixtureFiles.CreateXlsx(
            _tempDirectory,
            "registers.xlsx",
            sheetName: "Modbus",
            headers: ["Address", "Name", "Type", "Access"],
            rows: [["40002", "Pressure", "UINT16", "RO"]]);

        var pdfPath = MultiFormatFixtureFiles.CreatePdf(
            _tempDirectory,
            "maintenance.pdf",
            title: "Maintenance Manual",
            lines:
            [
                "Enclosure protection IP65 rating.",
                "Sensor calibration interval 12 months."
            ]);

        var pngPath = MultiFormatFixtureFiles.CreateImage(
            _tempDirectory,
            "scanned_table.png");

        // 3. Ingest Each Format and Assert Ingestion Succeeded
        var txtRes = await ingestionService.IngestFileAsync(txtPath);
        Assert.Equal(IngestionStatus.Succeeded, txtRes.Status);
        Assert.True(txtRes.ChunksIndexed > 0);

        var docxRes = await ingestionService.IngestFileAsync(docxPath);
        Assert.Equal(IngestionStatus.Succeeded, docxRes.Status);
        Assert.True(docxRes.ChunksIndexed > 0);

        var xlsxRes = await ingestionService.IngestFileAsync(xlsxPath);
        Assert.Equal(IngestionStatus.Succeeded, xlsxRes.Status);
        Assert.True(xlsxRes.ChunksIndexed > 0);

        var pdfRes = await ingestionService.IngestFileAsync(pdfPath);
        Assert.Equal(IngestionStatus.Succeeded, pdfRes.Status);
        Assert.True(pdfRes.ChunksIndexed > 0);

        var pngRes = await ingestionService.IngestFileAsync(pngPath);
        Assert.Equal(IngestionStatus.Succeeded, pngRes.Status);
        Assert.True(pngRes.ChunksIndexed > 0);

        // 4. Set up Retrieval & RAG Pipeline
        var retriever = new DenseRetriever(embedder, vectorStore);
        var pipeline = new RagPipeline(retriever, chatClient);

        // 5. Test Queries Against Each Format
        // Query TXT (Voltage)
        var responseTxt = await pipeline.AskAsync("Какое напряжение питания требуется для работы контроллера?");
        Assert.Contains("24 В", responseTxt.Answer, StringComparison.Ordinal);
        Assert.Contains("[S1]", responseTxt.Answer, StringComparison.Ordinal);
        Assert.NotEmpty(responseTxt.Sources);
        Assert.Contains(responseTxt.Sources, s => s.Chunk.Text.Contains("24 В", StringComparison.Ordinal));
        Assert.Equal(1, responseTxt.Diagnostics.ValidCitationCount);

        // Query DOCX (RS-485 Speed)
        var responseDocx = await pipeline.AskAsync("Какая скорость передачи данных установлена для интерфейса RS-485?");
        Assert.Contains("115200", responseDocx.Answer, StringComparison.Ordinal);
        Assert.Contains("RS-485", responseDocx.Answer, StringComparison.Ordinal);
        Assert.Contains("[S1]", responseDocx.Answer, StringComparison.Ordinal);
        Assert.Contains(responseDocx.Sources, s => s.Chunk.Text.Contains("115200", StringComparison.Ordinal));

        // Query XLSX (Register 40002 Pressure)
        var responseXlsx = await pipeline.AskAsync("Какой Holding Register отвечает за Pressure и каков его тип?");
        Assert.Contains("40002", responseXlsx.Answer, StringComparison.Ordinal);
        Assert.Contains("Pressure", responseXlsx.Answer, StringComparison.Ordinal);
        Assert.Contains("UINT16", responseXlsx.Answer, StringComparison.Ordinal);
        Assert.Contains(responseXlsx.Sources, s => s.Chunk.Text.Contains("40002", StringComparison.Ordinal));

        // Query PDF (IP65)
        var responsePdf = await pipeline.AskAsync("Какую степень защиты оболочки имеет корпус промышленного прибора?");
        Assert.Contains("IP65", responsePdf.Answer, StringComparison.Ordinal);
        Assert.Contains(responsePdf.Sources, s => s.Chunk.Text.Contains("IP65", StringComparison.Ordinal));

        // Query PNG / Scanned OCR (Register 40001)
        var responsePng = await pipeline.AskAsync("В каком регистре хранится температура устройства и каков её тип?");
        Assert.Contains("40001", responsePng.Answer, StringComparison.Ordinal);
        Assert.Contains("INT16", responsePng.Answer, StringComparison.Ordinal);
        Assert.Contains(responsePng.Sources, s => s.Chunk.Text.Contains("40001", StringComparison.Ordinal));
    }
}
