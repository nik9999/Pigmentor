using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Ingestion.Chunking;
using Pigmentor.Ingestion.Extraction;
using Pigmentor.Ingestion.Fingerprints;
using Pigmentor.Ingestion.Lifecycle;
using Pigmentor.Ingestion.Normalization;
using Pigmentor.Ingestion.Normalization.Rules;
using Pigmentor.Ingestion.Quality;
using Pigmentor.Ingestion.Services;
using Pigmentor.Tests.Fixtures;
using Pigmentor.VectorStore.SqliteVec;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Pigmentor.Tests;

public sealed class ExtractionAndNormalizationTests : IDisposable
{
    private readonly string _tempDbPath;
    private readonly string _connectionString;

    public ExtractionAndNormalizationTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"pigmentor_extract_{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_tempDbPath}";
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_tempDbPath))
        {
            try { File.Delete(_tempDbPath); } catch { }
        }
        var wal = _tempDbPath + "-wal";
        if (File.Exists(wal))
        {
            try { File.Delete(wal); } catch { }
        }
        var shm = _tempDbPath + "-shm";
        if (File.Exists(shm))
        {
            try { File.Delete(shm); } catch { }
        }
    }

    #region Helper Classes & Factories

    public sealed class FakeOcrEngine : IOcrEngine
    {
        public string DefaultText { get; set; }
        public double DefaultConfidence { get; set; }
        public IReadOnlyList<OcrLine>? Lines { get; set; }
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
        public int CallCount { get; private set; }

        public FakeOcrEngine(string defaultText = "", double defaultConfidence = 0.95, IReadOnlyList<OcrLine>? lines = null)
        {
            DefaultText = defaultText;
            DefaultConfidence = defaultConfidence;
            Lines = lines;
        }

        public async Task<OcrResult> RecognizeAsync(Stream imageStream, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }
            return new OcrResult(DefaultText, DefaultConfidence, Lines: Lines);
        }
    }

    private static DocumentMetadata CreateMetadata(string id, string path) =>
        new(
            Id: id,
            Silo: "technical-docs",
            Department: "Engineering",
            Office: "Montreal",
            ConfidentialityLevel: "Internal",
            DocumentType: "Reference",
            FiscalYear: 2026,
            Author: "Engineer",
            LastModified: new DateOnly(2026, 4, 1),
            Title: Path.GetFileNameWithoutExtension(path));

    private static byte[] CreateDocxBytes(
        string heading1,
        string heading2,
        string paragraph,
        (string[] Headers, string[][] Rows)? table = null)
    {
        using var stream = new MemoryStream();
        using (var wordDoc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var mainPart = wordDoc.AddMainDocumentPart();
            mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document();
            var body = new Body();

            // Heading 1
            if (!string.IsNullOrEmpty(heading1))
            {
                var p1 = new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                    new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(heading1)));
                body.Append(p1);
            }

            // Heading 2
            if (!string.IsNullOrEmpty(heading2))
            {
                var p2 = new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Heading2" }),
                    new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(heading2)));
                body.Append(p2);
            }

            // Paragraph
            if (!string.IsNullOrEmpty(paragraph))
            {
                var p = new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                    new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(paragraph)));
                body.Append(p);
            }

            // Table
            if (table.HasValue)
            {
                var tbl = new DocumentFormat.OpenXml.Wordprocessing.Table();
                var headerRow = new DocumentFormat.OpenXml.Wordprocessing.TableRow();
                foreach (var h in table.Value.Headers)
                {
                    headerRow.Append(new DocumentFormat.OpenXml.Wordprocessing.TableCell(
                        new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                            new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(h)))));
                }
                tbl.Append(headerRow);

                foreach (var rowData in table.Value.Rows)
                {
                    var dataRow = new DocumentFormat.OpenXml.Wordprocessing.TableRow();
                    foreach (var cellVal in rowData)
                    {
                        dataRow.Append(new DocumentFormat.OpenXml.Wordprocessing.TableCell(
                            new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                                new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(cellVal)))));
                    }
                    tbl.Append(dataRow);
                }

                body.Append(tbl);
            }

            mainPart.Document.Append(body);
            mainPart.Document.Save();
        }

        return stream.ToArray();
    }

    private static byte[] CreateXlsxBytes(
        string sheetName,
        string[] headers,
        string[][] rows)
    {
        using var stream = new MemoryStream();
        using (var spreadsheet = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbookPart = spreadsheet.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            worksheetPart.Worksheet = new Worksheet(sheetData);

            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = sheetName
            });

            // Header row
            var headerRow = new Row();
            foreach (var h in headers)
            {
                headerRow.Append(new Cell { DataType = CellValues.String, CellValue = new CellValue(h) });
            }
            sheetData.Append(headerRow);

            // Data rows
            foreach (var rowData in rows)
            {
                var row = new Row();
                foreach (var cell in rowData)
                {
                    row.Append(new Cell { DataType = CellValues.String, CellValue = new CellValue(cell) });
                }
                sheetData.Append(row);
            }

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    private static byte[] CreateSampleImageBytes(string format)
    {
        // Minimal valid image signatures
        if (format == ".png")
        {
            return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                    0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89];
        }
        if (format is ".jpg" or ".jpeg")
        {
            return [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x01, 0x00, 0x48, 0x00, 0x48, 0x00, 0x00, 0xFF, 0xD9];
        }
        if (format == ".bmp")
        {
            // Minimal 1x1 24bpp BMP
            var bmp = new byte[54];
            bmp[0] = 0x42; bmp[1] = 0x4D; // "BM"
            BitConverter.GetBytes(54).CopyTo(bmp, 2);
            BitConverter.GetBytes(54).CopyTo(bmp, 10);
            BitConverter.GetBytes(40).CopyTo(bmp, 14);
            BitConverter.GetBytes(1).CopyTo(bmp, 18);
            BitConverter.GetBytes(1).CopyTo(bmp, 22);
            BitConverter.GetBytes((short)1).CopyTo(bmp, 26);
            BitConverter.GetBytes((short)24).CopyTo(bmp, 28);
            return bmp;
        }

        throw new ArgumentException($"Unsupported image format {format}");
    }

    #endregion

    #region 1. Supported Formats & Magic Byte Validation Tests (§46, §1)

    [Fact]
    public void SupportedFormats_StrictlyMatchesSpecification()
    {
        var extractor = new DocumentExtractor();

        // Supported formats
        Assert.True(extractor.CanExtract(".pdf"));
        Assert.True(extractor.CanExtract(".docx"));
        Assert.True(extractor.CanExtract(".xlsx"));
        Assert.True(extractor.CanExtract(".txt"));
        Assert.True(extractor.CanExtract(".bmp"));
        Assert.True(extractor.CanExtract(".png"));
        Assert.True(extractor.CanExtract(".jpg"));
        Assert.True(extractor.CanExtract(".jpeg"));

        // Explicitly forbidden formats
        Assert.False(extractor.CanExtract(".doc"));
        Assert.False(extractor.CanExtract(".xls"));
        Assert.False(extractor.CanExtract(".ppt"));
        Assert.False(extractor.CanExtract(".pptx"));
        Assert.False(extractor.CanExtract(".html"));
        Assert.False(extractor.CanExtract(".epub"));
    }

    [Fact]
    public async Task UnsupportedFormat_ThrowsNotSupportedException()
    {
        var extractor = new DocumentExtractor();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("test content"));
        var meta = CreateMetadata("doc-1", "doc.epub");

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            extractor.ExtractAsync(stream, "doc.epub", meta));
    }

    [Theory]
    [InlineData("file.pdf", new byte[] { 0x00, 0x01, 0x02, 0x03 })] // Not %PDF
    [InlineData("file.docx", new byte[] { 0x00, 0x01, 0x02, 0x03 })] // Not PK..
    [InlineData("file.xlsx", new byte[] { 0x00, 0x01, 0x02, 0x03 })] // Not PK..
    [InlineData("file.png", new byte[] { 0x00, 0x01, 0x02, 0x03 })] // Not PNG magic
    [InlineData("file.jpg", new byte[] { 0x00, 0x01, 0x02, 0x03 })] // Not JPG magic
    [InlineData("file.bmp", new byte[] { 0x00, 0x01, 0x02, 0x03 })] // Not BM magic
    public async Task MismatchedMagicBytes_ThrowsInvalidDataException(string filename, byte[] payload)
    {
        var extractor = new DocumentExtractor(ocrEngine: new FakeOcrEngine());
        using var stream = new MemoryStream(payload);
        var meta = CreateMetadata("doc-1", filename);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            extractor.ExtractAsync(stream, filename, meta));
        Assert.Contains("signature", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OversizedFile_ThrowsInvalidOperationException()
    {
        var limits = new ExtractionLimits { MaxFileSizeBytes = 10 };
        var extractor = new DocumentExtractor(limits: limits);
        var bytes = new byte[20];
        using var stream = new MemoryStream(bytes);
        var meta = CreateMetadata("doc-1", "file.txt");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            extractor.ExtractAsync(stream, "file.txt", meta));
    }

    #endregion

    #region 2. Fake OCR Engine & Cancellation (§48, §45)

    [Fact]
    public async Task FakeOcrEngine_ReturnsConfiguredTextAndConfidence()
    {
        var ocr = new FakeOcrEngine("Modbus RTU RS-485", 0.98);
        using var stream = new MemoryStream(CreateSampleImageBytes(".png"));

        var result = await ocr.RecognizeAsync(stream);

        Assert.Equal("Modbus RTU RS-485", result.Text);
        Assert.Equal(0.98, result.Confidence);
        Assert.Equal(1, ocr.CallCount);
    }

    [Fact]
    public async Task FakeOcrEngine_CancellationPropagatesImmediately()
    {
        var ocr = new FakeOcrEngine("Some text") { Delay = TimeSpan.FromSeconds(5) };
        using var stream = new MemoryStream(CreateSampleImageBytes(".png"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            ocr.RecognizeAsync(stream, cts.Token));
    }

    #endregion

    #region 3. DOCX Extraction (§54)

    [Fact]
    public async Task DocxExtractor_ExtractsHeadingsParagraphsAndTable()
    {
        var tableData = (
            Headers: new[] { "Регистр", "Параметр", "Тип", "Доступ" },
            Rows: new[]
            {
                new[] { "40001", "Температура датчика", "INT16", "RO" },
                new[] { "40002", "Давление в контуре", "FLOAT32", "RO" }
            });

        var bytes = CreateDocxBytes(
            heading1: "1. Описание устройства",
            heading2: "1.1 Интерфейс связи RS-485",
            paragraph: "Устройство поддерживает протокол Modbus RTU со скоростью 115200 бит/с.",
            table: tableData);

        var extractor = new DocxFileExtractor();
        using var stream = new MemoryStream(bytes);
        var meta = CreateMetadata("docx-1", "manual.docx");

        var doc = await extractor.ExtractAsync(stream, "manual.docx", meta);

        Assert.Equal("docx-1", doc.DocumentId);
        Assert.Equal(".docx", doc.Format);
        Assert.Contains(doc.Blocks, b => b is HeadingBlock h && h.Text == "1. Описание устройства" && h.Level == 1);
        Assert.Contains(doc.Blocks, b => b is HeadingBlock h && h.Text == "1.1 Интерфейс связи RS-485" && h.Level == 2);
        Assert.Contains(doc.Blocks, b => b is TextBlock t && t.Text.Contains("Modbus RTU") && t.Text.Contains("115200"));

        var tableBlock = doc.Blocks.OfType<TableBlock>().Single();
        Assert.Equal(4, tableBlock.Table.Columns.Count);
        Assert.Equal(2, tableBlock.Table.Rows.Count);
        Assert.Equal("40001", tableBlock.Table.Rows[0].CellsByHeader["Регистр"]);
        Assert.Equal("Температура датчика", tableBlock.Table.Rows[0].CellsByHeader["Параметр"]);
        Assert.Equal("INT16", tableBlock.Table.Rows[0].CellsByHeader["Тип"]);
        Assert.Equal("RO", tableBlock.Table.Rows[0].CellsByHeader["Доступ"]);
    }

    #endregion

    #region 4. XLSX Extraction & Table Relationships (§53)

    [Fact]
    public async Task XlsxExtractor_ExtractsSheetAndTableWithProvenance()
    {
        var headers = new[] { "Регистр", "Описание", "Формат", "Доступ" };
        var rows = new[]
        {
            new[] { "40001", "Температура обмотки", "INT16", "RO" },
            new[] { "40002", "Ток фазы A", "FLOAT32", "RO" }
        };

        var bytes = CreateXlsxBytes("Modbus_Registers", headers, rows);
        var extractor = new XlsxFileExtractor();
        using var stream = new MemoryStream(bytes);
        var meta = CreateMetadata("xlsx-1", "registers.xlsx");

        var doc = await extractor.ExtractAsync(stream, "registers.xlsx", meta);

        Assert.Equal("xlsx-1", doc.DocumentId);
        Assert.Equal(".xlsx", doc.Format);

        var tableBlock = doc.Blocks.OfType<TableBlock>().Single();
        Assert.Equal("Modbus_Registers", tableBlock.Provenance.SheetName);
        Assert.Equal("Modbus_Registers", tableBlock.Table.SheetName);
        Assert.Equal("A1:D3", tableBlock.Provenance.CellRange);
        Assert.Equal(4, tableBlock.Table.Columns.Count);
        Assert.Equal(2, tableBlock.Table.Rows.Count);

        // Verify cell-to-header mapping
        var row1 = tableBlock.Table.Rows[0];
        Assert.Equal("40001", row1.CellsByHeader["Регистр"]);
        Assert.Equal("Температура обмотки", row1.CellsByHeader["Описание"]);
        Assert.Equal("INT16", row1.CellsByHeader["Формат"]);
        Assert.Equal("RO", row1.CellsByHeader["Доступ"]);

        // Verify Markdown representation contains pipes and headers
        var markdown = tableBlock.Table.ToMarkdownTable();
        Assert.Contains("| Регистр | Описание | Формат | Доступ |", markdown);
        Assert.Contains("| 40001 | Температура обмотки | INT16 | RO |", markdown);
    }

    #endregion

    #region 5. PDF Table Extraction & Multi-Page Continuation (§49, §50, §58)

    [Fact]
    public async Task PdfExtractor_ExtractsTableAndRowCellRelationships()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        // Layout a table with pipe delimiters (using standard ASCII/Latin characters)
        page.AddText("Device Registers Table", 12, new PdfPoint(50, 750), font);
        page.AddText("Register | Parameter | Type | Access", 12, new PdfPoint(50, 720), font);
        page.AddText("40001 | Temperature | INT16 | RO", 12, new PdfPoint(50, 700), font);
        page.AddText("40002 | Voltage | FLOAT32 | RO", 12, new PdfPoint(50, 680), font);

        var pdfBytes = builder.Build();
        var extractor = new PdfFileExtractor();
        using var stream = new MemoryStream(pdfBytes);
        var meta = CreateMetadata("pdf-1", "device_regs.pdf");

        var doc = await extractor.ExtractAsync(stream, "device_regs.pdf", meta);

        Assert.Equal("pdf-1", doc.DocumentId);
        var tableBlock = doc.Blocks.OfType<TableBlock>().Single();
        Assert.Equal(4, tableBlock.Table.Columns.Count);
        Assert.Equal(2, tableBlock.Table.Rows.Count);

        // Cell-to-header relationship check (Prompt §49: 40001 <-> Temperature <-> INT16 <-> RO)
        var row1 = tableBlock.Table.Rows[0];
        Assert.Equal("40001", row1.CellsByHeader["Register"]);
        Assert.Equal("Temperature", row1.CellsByHeader["Parameter"]);
        Assert.Equal("INT16", row1.CellsByHeader["Type"]);
        Assert.Equal("RO", row1.CellsByHeader["Access"]);
    }

    [Fact]
    public async Task PdfExtractor_MultiPageTableContinuation_MergesAcrossPages()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        // Page 1: Table starts
        var page1 = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        page1.AddText("Table 1 - Configuration", 12, new PdfPoint(50, 750), font);
        page1.AddText("Address | Name | Type", 12, new PdfPoint(50, 720), font);
        page1.AddText("40001 | Register 1 | INT16", 12, new PdfPoint(50, 700), font);
        page1.AddText("40002 | Register 2 | INT16", 12, new PdfPoint(50, 680), font);

        // Page 2: Table continues
        var page2 = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        page2.AddText("Table 1 - Continued", 12, new PdfPoint(50, 750), font);
        page2.AddText("Address | Name | Type", 12, new PdfPoint(50, 720), font);
        page2.AddText("40003 | Register 3 | INT16", 12, new PdfPoint(50, 700), font);
        page2.AddText("40004 | Register 4 | INT16", 12, new PdfPoint(50, 680), font);

        var pdfBytes = builder.Build();
        var extractor = new PdfFileExtractor();
        using var stream = new MemoryStream(pdfBytes);
        var meta = CreateMetadata("pdf-multipage", "multipage.pdf");

        var doc = await extractor.ExtractAsync(stream, "multipage.pdf", meta);

        // The continuation detector merges into a single table spanning pages 1 to 2!
        var tableBlock = Assert.Single(doc.Blocks.OfType<TableBlock>());
        Assert.Equal(1, tableBlock.Provenance.PageStart);
        Assert.Equal(2, tableBlock.Provenance.PageEnd);
        Assert.Equal(4, tableBlock.Table.Rows.Count);
    }

    [Fact]
    public void FigureBlock_CombinesCaptionSurroundingContextAndOcrText()
    {
        var prov = new BlockProvenance(
            DocumentId: "doc-1",
            PageNumber: 4,
            FigureId: "Рисунок 1",
            TableCaption: "Рисунок 1 - Схема подключения RS-485");

        var figure = new FigureBlock(
            BlockId: "doc-1#fig1",
            FigureId: "Рисунок 1",
            Caption: "Рисунок 1 - Схема подключения RS-485",
            OcrText: "Линия А, Линия B, Терминатор 120 Ω, GND",
            SurroundingContext: "Подключение двухпроводной линии связи к клеммам X1.",
            AssetReference: null,
            Provenance: prov);

        Assert.Contains("Рисунок 1 - Схема подключения RS-485", figure.Text);
        Assert.Contains("Подключение двухпроводной линии связи к клеммам X1.", figure.Text);
        Assert.Contains("Линия А, Линия B, Терминатор 120 Ω, GND", figure.Text);
    }

    #endregion

    #region 6. Table Chunking Repeating Headers & Row Provenance (§53, §49)

    [Fact]
    public void TableChunker_RepeatsHeadersAcrossAllChunks()
    {
        var columns = new List<string> { "Регистр", "Параметр", "Значение" };
        var rows = new List<ExtractedTableRow>();
        for (var i = 1; i <= 25; i++)
        {
            var rStr = (40000 + i).ToString(CultureInfo.InvariantCulture);
            var vStr = (i * 10).ToString(CultureInfo.InvariantCulture);
            var dict = new Dictionary<string, string>
            {
                ["Регистр"] = rStr,
                ["Параметр"] = $"Параметр {i}",
                ["Значение"] = vStr
            };
            rows.Add(new ExtractedTableRow(i, [rStr, $"Параметр {i}", vStr], dict));
        }

        var table = new ExtractedTable(
            TableId: "tbl-1",
            Caption: "Таблица конфигурации",
            Columns: columns,
            Rows: rows,
            PageStart: 1,
            PageEnd: 1,
            SheetName: "Modbus_Config");

        var provenance = new BlockProvenance(
            DocumentId: "doc-table",
            PageNumber: 1,
            PageStart: 1,
            PageEnd: 1,
            SheetName: "Modbus_Config",
            TableId: "tbl-1",
            TableCaption: "Таблица конфигурации");

        var tableBlock = new TableBlock("doc-table#tbl1", table, provenance);
        var meta = CreateMetadata("doc-table", "config.xlsx");
        var counter = 0;

        var chunks = TableChunker.ChunkTable(tableBlock, meta, ref counter, maxRowsPerChunk: 5);

        Assert.Equal(5, chunks.Count);
        foreach (var chunk in chunks)
        {
            // Each chunk must contain the column headers!
            Assert.Contains("| Регистр | Параметр | Значение |", chunk.Text);
            Assert.Contains("| --- | --- | --- |", chunk.Text);
        }

        // Verify row provenance progression
        Assert.Contains("Стр. 1", chunks[0].Provenance);
        Assert.Contains("Лист: Modbus_Config", chunks[0].Provenance);
        Assert.Contains("Строки 2–6", chunks[0].Provenance);
        Assert.Contains("Строки 22–26", chunks[4].Provenance);
    }

    #endregion

    #region 7. Image OCR & Graceful Fallback (§55, §57, §66)

    [Fact]
    public async Task ImageExtractor_WithFakeOcr_ExtractsTechnicalSymbols()
    {
        var expectedOcrText = "Схема RS-485: терминатор 120 Ω, ток петли 4-20 мА, контакт GND.";
        var fakeOcr = new FakeOcrEngine(expectedOcrText, 0.96);
        var extractor = new ImageFileExtractor(fakeOcr);

        using var stream = new MemoryStream(CreateSampleImageBytes(".png"));
        var meta = CreateMetadata("img-1", "schema.png");

        var doc = await extractor.ExtractAsync(stream, "schema.png", meta);

        Assert.Equal("img-1", doc.DocumentId);
        Assert.Equal(".png", doc.Format);
        var textBlock = doc.Blocks.OfType<TextBlock>().Single();
        Assert.Contains("120 Ω", textBlock.Text);
        Assert.Contains("4-20 мА", textBlock.Text);
        Assert.Contains("RS-485", textBlock.Text);
    }

    [Fact]
    public async Task StandaloneImage_WithoutOcrEngine_ThrowsClearInvalidOperationException()
    {
        var extractor = new ImageFileExtractor(ocrEngine: null);
        using var stream = new MemoryStream(CreateSampleImageBytes(".png"));
        var meta = CreateMetadata("img-1", "schema.png");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            extractor.ExtractAsync(stream, "schema.png", meta));
        Assert.Contains("no IOcrEngine is configured", ex.Message);
    }

    [Fact]
    public async Task ImageExtractor_JPGandJPEG_UseIdenticalPath()
    {
        var fakeOcr = new FakeOcrEngine("Test OCR text", 0.95);
        var extractor = new ImageFileExtractor(fakeOcr);

        using var jpgStream = new MemoryStream(CreateSampleImageBytes(".jpg"));
        var metaJpg = CreateMetadata("jpg-1", "photo.jpg");
        var docJpg = await extractor.ExtractAsync(jpgStream, "photo.jpg", metaJpg);

        using var jpegStream = new MemoryStream(CreateSampleImageBytes(".jpeg"));
        var metaJpeg = CreateMetadata("jpeg-1", "photo.jpeg");
        var docJpeg = await extractor.ExtractAsync(jpegStream, "photo.jpeg", metaJpeg);

        Assert.Equal(".jpg", docJpg.Format);
        Assert.Equal(".jpeg", docJpeg.Format);
        Assert.Equal(2, fakeOcr.CallCount);
    }

    #endregion

    #region 8. OCR Quality Gate (§56)

    [Fact]
    public void OcrQualityGate_AcceptsCleanTechnicalText()
    {
        var assessment = OcrQualityGate.Assess("RS-485 Modbus RTU 115200 8-N-1 Holding Register 40001, 120 Ω, ±0.5%");

        Assert.True(assessment.IsAcceptable);
        Assert.True(assessment.Score >= 0.7);
    }

    [Theory]
    [InlineData("|||| ||| ~~ ### \uFFFD @@@ %%% $$$ ^^^")] // Noise and symbols
    [InlineData("a a a a a a a a a a a a a a a a a a a a")] // Repeated single char
    [InlineData("")] // Empty text
    [InlineData("           ")] // Whitespace
    public void OcrQualityGate_RejectsGarbageAndRepetitiveNoise(string garbageText)
    {
        var assessment = OcrQualityGate.Assess(garbageText);

        Assert.False(assessment.IsAcceptable);
        Assert.True(assessment.Score < 0.6);
        Assert.NotEmpty(assessment.Reasons);
    }

    #endregion

    #region 9. Normalization Pipeline Rules (§34, §35, §51, §52, §26, §27)

    [Fact]
    public void UnicodeNormalizationRule_NormalizesFormsAndPreservesTechnicalSymbols()
    {
        // Soft-hyphen (\u00AD), zero-width space (\u200B), non-breaking space (\u00A0), technical symbols: ° Ω µ ± ≤ ≥
        var input = "Температура: 25\u00A0°C,\u200B сопротивление: 120\u00A0Ω,\u00AD погрешность: ±0.5%, ток ≤ 20\u00A0мА.";
        var block = new TextBlock("b1", input, new BlockProvenance("doc-1", 1));

        var result = UnicodeNormalizationRule.Normalize(block);

        var normalized = result.Text;
        Assert.Contains("°C", normalized);
        Assert.Contains("120 Ω", normalized);
        Assert.Contains("±0.5%", normalized);
        Assert.Contains("≤ 20 мА", normalized);
        Assert.DoesNotContain("\u00AD", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("\u200B", normalized, StringComparison.Ordinal);
        Assert.DoesNotContain("\u00A0", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void DehyphenationRule_RepairsRussianHyphensAndPreservesTechnicalTerms()
    {
        var diagnostics = new NormalizationDiagnostics();

        // Russian hyphenated words wrapped across lines should be repaired
        // Technical terms like RS-485, 4-20 мА, 4-20 mA, клиент-сервер, -10 °C should NOT be corrupted!
        var input = "Выполняется конфи-\nгурация интерфейса RS-485 для работы по схеме клиент-сервер при диапазоне -10 °C и токе 4-20 мА. Протокол Modbus-\nTCP готов.";
        var block = new TextBlock("b1", input, new BlockProvenance("doc-1", 1));

        var result = DehyphenationRule.Normalize(block, diagnostics);

        var normalized = result.Text;
        Assert.Contains("конфигурация", normalized);
        Assert.Contains("RS-485", normalized);
        Assert.Contains("клиент-сервер", normalized);
        Assert.Contains("-10 °C", normalized);
        Assert.Contains("4-20 мА", normalized);
        Assert.Contains("Modbus TCP", normalized);
        Assert.True(diagnostics.DehyphenationRepairs >= 1);
    }

    [Fact]
    public async Task HeaderFooterRemovalRule_RemovesRepeatedHeadersFootersAcrossThreePages()
    {
        var normalizer = new DocumentNormalizer();

        const string headerText = "БЭ2704 Руководство по эксплуатации";
        const string footerText = "Конфиденциально - НПП ЭКРА";

        var blocks = new List<ExtractedBlock>
        {
            // Page 1
            new TextBlock("b1", headerText, new BlockProvenance("doc-1", 1)),
            new TextBlock("b2", "Содержимое страницы 1 с важной информацией.", new BlockProvenance("doc-1", 1)),
            new TextBlock("b3", footerText, new BlockProvenance("doc-1", 1)),

            // Page 2
            new TextBlock("b4", headerText, new BlockProvenance("doc-1", 2)),
            new TextBlock("b5", "Содержимое страницы 2 с таблицей регистров.", new BlockProvenance("doc-1", 2)),
            new TextBlock("b6", footerText, new BlockProvenance("doc-1", 2)),

            // Page 3
            new TextBlock("b7", headerText, new BlockProvenance("doc-1", 3)),
            new TextBlock("b8", "Содержимое страницы 3 с графиком испытаний.", new BlockProvenance("doc-1", 3)),
            new TextBlock("b9", footerText, new BlockProvenance("doc-1", 3)),
        };

        var meta = CreateMetadata("doc-1", "doc.pdf");
        var extracted = new ExtractedDocument("doc-1", "doc.pdf", ".pdf", blocks, meta);
        var normalized = await normalizer.NormalizeAsync(extracted);

        // Header and footer should be stripped from all pages and empty blocks pruned
        Assert.Equal(3, normalized.Blocks.Count);
        Assert.All(normalized.Blocks, b => Assert.DoesNotContain(headerText, b.Text));
        Assert.All(normalized.Blocks, b => Assert.DoesNotContain(footerText, b.Text));
        Assert.True(normalized.Diagnostics.RemovedHeaders + normalized.Diagnostics.RemovedFooters >= 6);
    }

    [Fact]
    public void PageNumberNoiseRule_StripsPageNumberNoiseFromSearchableText()
    {
        var diagnostics = new NormalizationDiagnostics();

        var b1 = PageNumberNoiseRule.Normalize(new TextBlock("b1", "12", new BlockProvenance("doc-1", 12)), diagnostics);
        var b2 = PageNumberNoiseRule.Normalize(new TextBlock("b2", "- 12 -", new BlockProvenance("doc-1", 12)), diagnostics);
        var b3 = PageNumberNoiseRule.Normalize(new TextBlock("b3", "Стр. 12 из 100", new BlockProvenance("doc-1", 12)), diagnostics);
        var b4 = PageNumberNoiseRule.Normalize(new TextBlock("b4", "Технический текст документа.", new BlockProvenance("doc-1", 12)), diagnostics);

        Assert.Equal(string.Empty, b1.Text);
        Assert.Equal(string.Empty, b2.Text);
        Assert.Equal(string.Empty, b3.Text);
        Assert.Equal("Технический текст документа.", b4.Text);
        Assert.Equal(12, b4.Provenance.PageNumber);
        Assert.True(diagnostics.RemovedPageNumbers >= 3);
    }

    [Fact]
    public async Task EmptyBlockPruningAndDeduplication_RemovesDuplicatesAndPrunesEmptyPages()
    {
        var normalizer = new DocumentNormalizer();
        var blocks = new List<ExtractedBlock>
        {
            // Page 1: clean blocks
            new TextBlock("b1", "Уникальный текст 1", new BlockProvenance("doc-1", 1)),
            new TextBlock("b2", "Уникальный текст 1", new BlockProvenance("doc-1", 1)), // Duplicate!

            // Page 2: empty/whitespace only blocks
            new TextBlock("b3", "   \n\t  ", new BlockProvenance("doc-1", 2)),
            new TextBlock("b4", "", new BlockProvenance("doc-1", 2)),

            // Page 3: valid block
            new TextBlock("b5", "Уникальный текст 3", new BlockProvenance("doc-1", 3))
        };

        var meta = CreateMetadata("doc-1", "doc.txt");
        var extracted = new ExtractedDocument("doc-1", "doc.txt", "txt", blocks, meta);
        var normalized = await normalizer.NormalizeAsync(extracted);

        Assert.Equal(2, normalized.Blocks.Count);
        Assert.Equal("Уникальный текст 1", normalized.Blocks[0].Text);
        Assert.Equal("Уникальный текст 3", normalized.Blocks[1].Text);
        Assert.True(normalized.Diagnostics.DuplicateBlocksRemoved >= 1);
        Assert.True(normalized.Diagnostics.DroppedEmptyPages >= 1);
    }

    #endregion

    #region 10. Normalization Versioning & Reingestion Invariants (§40-§43)

    [Fact]
    public void NormalizationVersion_ChangesEffectiveContentHash()
    {
        const string rawContent = "Some technical document raw text";

        var hashV1 = IngestionFingerprint.ComputeEffectiveContentHash(rawContent, normalizationVersion: 1);
        var hashV2 = IngestionFingerprint.ComputeEffectiveContentHash(rawContent, normalizationVersion: 2);

        Assert.Equal(1, IngestionFingerprint.DefaultNormalizationVersion);
        Assert.NotNull(hashV1);
        Assert.NotNull(hashV2);
        Assert.NotEqual(hashV1, hashV2);
    }

    #endregion

    #region 11. End-to-End File Ingestion via DocumentIngestionService (§1, §2, §40)

    [Fact]
    public async Task IngestFileAsync_EndToEnd_ExtractionNormalizationAndVectorStore()
    {
        // 1. Setup SQLite vector store & state store
        using var vectorStore = new SqliteVecStore(new SqliteVecStoreOptions
        {
            ConnectionString = _connectionString,
            Dimensions = 2,
            EmbeddingModel = "test-model"
        });

        using var stateStore = new SqliteIngestionStateStore(_connectionString);

        var embedder = new TestEmbeddingService(2, "test-model");
        var extractor = new DocumentExtractor();
        var normalizer = new DocumentNormalizer();
        var chunker = new StructuralDocumentChunker();

        var service = new DocumentIngestionService(
            stateStore: stateStore,
            vectorStore: vectorStore,
            chunker: chunker,
            embedder: embedder,
            documentExtractor: extractor,
            documentNormalizer: normalizer,
            logger: NullLogger<DocumentIngestionService>.Instance);

        // 2. Create in-memory DOCX document and write to temp file
        var tempDocxPath = Path.Combine(Path.GetTempPath(), $"manual_{Guid.NewGuid():N}.docx");
        try
        {
            var docxBytes = CreateDocxBytes(
                heading1: "1. Инструкция по эксплуатации",
                heading2: "1.1 Протокол RS-485",
                paragraph: "Устройство БЭ2704 передает данные по RS-485 на скорости 115200 бит/с.",
                table: (Headers: ["Регистр", "Параметр"], Rows: [["40001", "Температура"]]));

            await File.WriteAllBytesAsync(tempDocxPath, docxBytes);

            var metadata = new DocumentMetadata(
                Id: "doc-manual-1",
                Silo: "technical-docs",
                Department: "Engineering",
                Office: "Montreal",
                ConfidentialityLevel: "Internal",
                DocumentType: "Reference",
                FiscalYear: 2026,
                Author: "Engineer",
                LastModified: new DateOnly(2026, 4, 1),
                Title: "Инструкция БЭ2704");

            // 3. First ingestion: IngestFileAsync
            var result1 = await service.IngestFileAsync(tempDocxPath, metadata);

            Assert.Equal(IngestionStatus.Succeeded, result1.Status);
            Assert.False(result1.Skipped);
            Assert.Equal(1, result1.Version);
            Assert.True(result1.ChunksIndexed > 0);

            var stored = await stateStore.GetAsync("doc-manual-1");
            Assert.NotNull(stored);
            Assert.Equal(IngestionStatus.Succeeded, stored.Status);
            Assert.Equal(1, stored.Version);

            // 4. Ingest unchanged document again => Skipped == true
            var result2 = await service.IngestFileAsync(tempDocxPath, metadata);
            Assert.True(result2.Skipped);
            Assert.Equal(1, result2.Version);

            // 5. Ingest updated document => transitions to version 2
            var updatedDocxBytes = CreateDocxBytes(
                heading1: "1. Инструкция по эксплуатации v2",
                heading2: "1.1 Протокол RS-485",
                paragraph: "Обновленная редакция: Устройство БЭ2704 работает на скорости 57600 бит/с.");

            await File.WriteAllBytesAsync(tempDocxPath, updatedDocxBytes);
            var result3 = await service.IngestFileAsync(tempDocxPath, metadata);

            Assert.Equal(IngestionStatus.Succeeded, result3.Status);
            Assert.False(result3.Skipped);
            Assert.Equal(2, result3.Version);

            var stored2 = await stateStore.GetAsync("doc-manual-1");
            Assert.NotNull(stored2);
            Assert.Equal(IngestionStatus.Succeeded, stored2.Status);
            Assert.Equal(2, stored2.Version);
        }
        finally
        {
            if (File.Exists(tempDocxPath))
            {
                try { File.Delete(tempDocxPath); } catch { }
            }
        }
    }

    #endregion

    #region Helper Test Embedding Service

    private sealed class TestEmbeddingService : IEmbeddingService
    {
        public int Dimensions { get; }
        public string EmbeddingModel { get; }

        public TestEmbeddingService(int dimensions = 2, string embeddingModel = "test-model")
        {
            Dimensions = dimensions;
            EmbeddingModel = embeddingModel;
        }

        public Task<EmbeddedChunk> EmbedAsync(DocumentChunk chunk, CancellationToken cancellationToken = default)
        {
            var vec = new float[Dimensions];
            vec[0] = 1f;
            return Task.FromResult(new EmbeddedChunk(chunk, vec, EmbeddingModel));
        }

        public Task<ReadOnlyMemory<float>> EmbedQueryAsync(string query, CancellationToken cancellationToken = default)
        {
            var vec = new float[Dimensions];
            vec[0] = 1f;
            return Task.FromResult<ReadOnlyMemory<float>>(vec);
        }

        public async IAsyncEnumerable<EmbeddedChunk> EmbedAsync(
            IAsyncEnumerable<DocumentChunk> chunks,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var chunk in chunks.WithCancellation(cancellationToken))
            {
                yield return await EmbedAsync(chunk, cancellationToken);
            }
        }
    }

    #endregion

    #region Hardening v1 Tests - PDF Figures, Scanned Tables, and Normalization Lifecycle

    [Fact]
    public async Task PdfExtractor_FigureReceivesBoundedSurroundingContext_AndPreservesTokens()
    {
        var pdfBytes = TestPdfFixtures.CreatePdfFigurePdf();
        var fakeOcr = new FakeOcrEngine("A\nB\nGND\n120 Ω\nRS-485", 0.98);
        var extractor = new PdfFileExtractor(fakeOcr);
        using var ms = new MemoryStream(pdfBytes);
        var meta = CreateMetadata("pdf-fig", "figure_test.pdf");

        var doc = await extractor.ExtractAsync(ms, "figure_test.pdf", meta);

        var figure = Assert.Single(doc.Blocks.OfType<FigureBlock>());
        Assert.Equal("Рисунок 1", figure.FigureId);
        Assert.Contains("Figure 12 - Modbus RTU Wiring Schematic", figure.Caption);
        Assert.Contains("Connection of RS-485 interface uses a two-wire line", figure.SurroundingContext);
        Assert.Contains("Install a 120 Ohm termination resistor", figure.SurroundingContext);
        Assert.DoesNotContain("\n12", figure.SurroundingContext);
        Assert.False(figure.SurroundingContext.EndsWith(" 12", StringComparison.Ordinal));

        var searchable = figure.Text;
        Assert.Contains("Контекст:", searchable);
        Assert.Contains("Текст на рисунке:", searchable);
        Assert.Contains("RS-485", searchable);
        Assert.Contains("Modbus RTU", searchable);
        Assert.Contains("120 Ω", searchable);
        Assert.Contains("A", searchable);
        Assert.Contains("B", searchable);
        Assert.Contains("GND", searchable);
    }

    [Fact]
    public async Task PdfExtractor_LowQualityFigureOcr_RejectedWithoutPollutingSearchableText()
    {
        var pdfBytes = TestPdfFixtures.CreatePdfFigurePdf();
        var fakeOcr = new FakeOcrEngine("###@@@$$$%%%&&&!!!???~~~^^^", 0.20);
        var extractor = new PdfFileExtractor(fakeOcr);
        using var ms = new MemoryStream(pdfBytes);
        var meta = CreateMetadata("pdf-fig-garbage", "garbage_figure.pdf");

        var doc = await extractor.ExtractAsync(ms, "garbage_figure.pdf", meta);

        var figure = Assert.Single(doc.Blocks.OfType<FigureBlock>());
        Assert.Empty(figure.OcrText);
        Assert.NotEmpty(figure.Caption);
        Assert.NotEmpty(figure.SurroundingContext);
        Assert.DoesNotContain("Текст на рисунке:", figure.Text);
        Assert.DoesNotContain("###@@@", figure.Text);
    }

    [Fact]
    public void OcrTableDetector_DetectsStructuredModbusTable()
    {
        var detector = new OcrTableDetector();
        const string ocrText = """
            Address | Name | Type | Access
            40001 | Temperature | INT16 | RO
            40002 | Pressure | UINT16 | RO
            """;

        var ocrResult = new OcrResult(ocrText, 0.95);
        var detected = detector.TryDetectTable(ocrResult, "doc-1", 1, "Modbus Table", out var table);

        Assert.True(detected);
        Assert.NotNull(table);
        Assert.Equal(4, table.Columns.Count);
        Assert.Equal(["Address", "Name", "Type", "Access"], table.Columns);
        Assert.Equal(2, table.Rows.Count);

        var row1 = table.Rows[0];
        Assert.Equal("40001", row1.CellsByHeader["Address"]);
        Assert.Equal("Temperature", row1.CellsByHeader["Name"]);
        Assert.Equal("INT16", row1.CellsByHeader["Type"]);
        Assert.Equal("RO", row1.CellsByHeader["Access"]);

        var row2 = table.Rows[1];
        Assert.Equal("40002", row2.CellsByHeader["Address"]);
        Assert.Equal("Pressure", row2.CellsByHeader["Name"]);
        Assert.Equal("UINT16", row2.CellsByHeader["Type"]);
        Assert.Equal("RO", row2.CellsByHeader["Access"]);
    }

    [Fact]
    public void OcrTableDetector_DetectsGenericTableFixture()
    {
        var detector = new OcrTableDetector();
        const string ocrText = """
            Parameter | Value | Unit
            Voltage | 24 | V
            Current | 4-20 | mA
            """;

        var ocrResult = new OcrResult(ocrText, 0.95);
        var detected = detector.TryDetectTable(ocrResult, "doc-2", 1, "Generic Table", out var table);

        Assert.True(detected);
        Assert.NotNull(table);
        Assert.Equal(3, table.Columns.Count);
        Assert.Equal(["Parameter", "Value", "Unit"], table.Columns);
        Assert.Equal(2, table.Rows.Count);

        Assert.Equal("Voltage", table.Rows[0].CellsByHeader["Parameter"]);
        Assert.Equal("24", table.Rows[0].CellsByHeader["Value"]);
        Assert.Equal("V", table.Rows[0].CellsByHeader["Unit"]);

        Assert.Equal("Current", table.Rows[1].CellsByHeader["Parameter"]);
        Assert.Equal("4-20", table.Rows[1].CellsByHeader["Value"]);
        Assert.Equal("mA", table.Rows[1].CellsByHeader["Unit"]);
    }

    [Fact]
    public void OcrTableDetector_DetectsSpaceDelimitedTable()
    {
        var detector = new OcrTableDetector();
        const string ocrText = "Address     Name          Type    Access\n40001       Temperature   INT16   RO\n40002       Pressure      UINT16  RO";

        var ocrResult = new OcrResult(ocrText, 0.95);
        var detected = detector.TryDetectTable(ocrResult, "doc-space", 1, "Space Table", out var table);

        Assert.True(detected);
        Assert.NotNull(table);
        Assert.Equal(4, table.Columns.Count);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("40001", table.Rows[0].CellsByHeader["Address"]);
        Assert.Equal("Pressure", table.Rows[1].CellsByHeader["Name"]);
    }

    [Fact]
    public void OcrTableDetector_OrdinaryProse_NotClassifiedAsTable()
    {
        var detector = new OcrTableDetector();
        const string prose = """
            Устройство поддерживает Modbus RTU.
            Подключение выполняется через RS-485.
            """;

        var ocrResult = new OcrResult(prose, 0.95);
        var detected = detector.TryDetectTable(ocrResult, "doc-prose", 1, "Prose", out var table);

        Assert.False(detected);
        Assert.Null(table);
    }

    [Fact]
    public void OcrTableDetector_LowQualityOcr_NotClassifiedAsTable()
    {
        var detector = new OcrTableDetector();
        const string garbage = "%%%%% $$$$$ ##### @@@@@";

        var ocrResult = new OcrResult(garbage, 0.15);
        var detected = detector.TryDetectTable(ocrResult, "doc-noise", 1, "Noise", out var table);

        Assert.False(detected);
        Assert.Null(table);
    }

    [Fact]
    public async Task PdfExtractor_ScannedPageWithTable_RoutesToTableBlockWithoutDuplicateTextBlock()
    {
        var pdfBytes = TestPdfFixtures.CreateScannedTablePdf();
        const string tableOcr = """
            Address | Name | Type | Access
            40001 | Temperature | INT16 | RO
            40002 | Pressure | UINT16 | RO
            """;

        var fakeOcr = new FakeOcrEngine(tableOcr, 0.95);
        var extractor = new PdfFileExtractor(fakeOcr);
        using var ms = new MemoryStream(pdfBytes);
        var meta = CreateMetadata("scanned-table-doc", "scanned_table.pdf");

        var doc = await extractor.ExtractAsync(ms, "scanned_table.pdf", meta);

        var tableBlock = Assert.Single(doc.Blocks.OfType<TableBlock>());
        Assert.Empty(doc.Blocks.OfType<TextBlock>());
        Assert.Equal(4, tableBlock.Table.Columns.Count);
        Assert.Equal(2, tableBlock.Table.Rows.Count);
        Assert.Equal("Temperature", tableBlock.Table.Rows[0].CellsByHeader["Name"]);
    }

    [Fact]
    public async Task PdfExtractor_ScannedPageWithProse_RoutesToTextBlockWithoutTableBlock()
    {
        var pdfBytes = TestPdfFixtures.CreateScannedPagePdf();
        const string proseOcr = "Устройство поддерживает Modbus RTU. Подключение выполняется через RS-485.";

        var fakeOcr = new FakeOcrEngine(proseOcr, 0.95);
        var extractor = new PdfFileExtractor(fakeOcr);
        using var ms = new MemoryStream(pdfBytes);
        var meta = CreateMetadata("scanned-prose-doc", "scanned_prose.pdf");

        var doc = await extractor.ExtractAsync(ms, "scanned_prose.pdf", meta);

        var textBlock = Assert.Single(doc.Blocks.OfType<TextBlock>());
        Assert.Empty(doc.Blocks.OfType<TableBlock>());
        Assert.Contains("Modbus RTU", textBlock.Text);
    }

    [Fact]
    public async Task ImageExtractor_StandaloneImageTable_RoutesToTableBlockWithoutDuplicateTextBlock()
    {
        var pngBytes = TestPdfFixtures.MinimalValidPng;
        const string tableOcr = """
            Address | Name | Type | Access
            40001 | Temperature | INT16 | RO
            40002 | Pressure | UINT16 | RO
            """;

        var fakeOcr = new FakeOcrEngine(tableOcr, 0.95);
        var extractor = new ImageFileExtractor(fakeOcr);
        using var ms = new MemoryStream(pngBytes);
        var meta = CreateMetadata("image-table-doc", "image_table.png");

        var doc = await extractor.ExtractAsync(ms, "image_table.png", meta);

        var tableBlock = Assert.Single(doc.Blocks.OfType<TableBlock>());
        Assert.Empty(doc.Blocks.OfType<TextBlock>());
        Assert.Equal(4, tableBlock.Table.Columns.Count);
        Assert.Equal(2, tableBlock.Table.Rows.Count);
        Assert.Equal("INT16", tableBlock.Table.Rows[0].CellsByHeader["Type"]);
    }

    [Fact]
    public async Task PdfExtractor_EmbeddedImageTable_RoutesToTableBlockInsteadOfFigureBlock()
    {
        var pdfBytes = TestPdfFixtures.CreatePdfFigurePdf();
        const string tableOcr = """
            Address | Name | Type | Access
            40001 | Temperature | INT16 | RO
            40002 | Pressure | UINT16 | RO
            """;

        var fakeOcr = new FakeOcrEngine(tableOcr, 0.95);
        var extractor = new PdfFileExtractor(fakeOcr);
        using var ms = new MemoryStream(pdfBytes);
        var meta = CreateMetadata("pdf-embedded-table", "embedded_table.pdf");

        var doc = await extractor.ExtractAsync(ms, "embedded_table.pdf", meta);

        var tableBlock = Assert.Single(doc.Blocks.OfType<TableBlock>());
        Assert.Empty(doc.Blocks.OfType<FigureBlock>());
        Assert.Equal(4, tableBlock.Table.Columns.Count);
        Assert.Equal(2, tableBlock.Table.Rows.Count);
    }

    [Fact]
    public async Task PdfExtractor_RealPdfTableFixture_ExtractsTableBlockWithAssociativeRows()
    {
        var pdfBytes = TestPdfFixtures.CreatePdfTablePdf();
        var extractor = new PdfFileExtractor();
        using var ms = new MemoryStream(pdfBytes);
        var meta = CreateMetadata("pdf-real-table", "pdf_table.pdf");

        var doc = await extractor.ExtractAsync(ms, "pdf_table.pdf", meta);

        var tableBlock = Assert.Single(doc.Blocks.OfType<TableBlock>());
        Assert.Equal(4, tableBlock.Table.Columns.Count);
        Assert.Equal(3, tableBlock.Table.Rows.Count);

        var row1 = tableBlock.Table.Rows[0];
        Assert.Equal("40001", row1.CellsByHeader["Address"]);
        Assert.Equal("Temperature", row1.CellsByHeader["Name"]);
        Assert.Equal("INT16", row1.CellsByHeader["Type"]);
        Assert.Equal("RO", row1.CellsByHeader["Access"]);

        var row2 = tableBlock.Table.Rows[1];
        Assert.Equal("40002", row2.CellsByHeader["Address"]);
        Assert.Equal("Pressure", row2.CellsByHeader["Name"]);
        Assert.Equal("UINT16", row2.CellsByHeader["Type"]);
        Assert.Equal("RO", row2.CellsByHeader["Access"]);

        var row3 = tableBlock.Table.Rows[2];
        Assert.Equal("40003", row3.CellsByHeader["Address"]);
        Assert.Equal("Humidity", row3.CellsByHeader["Name"]);
        Assert.Equal("INT16", row3.CellsByHeader["Type"]);
        Assert.Equal("RO", row3.CellsByHeader["Access"]);
    }

    [Fact]
    public async Task PdfExtractor_MultiPagePdfTableFixture_MergesAcrossPagesAndFiltersRepeatedHeader()
    {
        var pdfBytes = TestPdfFixtures.CreateMultiPageTablePdf();
        var extractor = new PdfFileExtractor();
        using var ms = new MemoryStream(pdfBytes);
        var meta = CreateMetadata("pdf-multipage-real", "multipage_table.pdf");

        var doc = await extractor.ExtractAsync(ms, "multipage_table.pdf", meta);

        var tableBlock = Assert.Single(doc.Blocks.OfType<TableBlock>());
        Assert.Equal(1, tableBlock.Provenance.PageStart);
        Assert.Equal(2, tableBlock.Provenance.PageEnd);

        // Crucial test (§25): Repeated header row from page 2 must NOT be treated as a data row!
        Assert.Equal(4, tableBlock.Table.Rows.Count);

        Assert.Equal("40001", tableBlock.Table.Rows[0].CellsByHeader["Address"]);
        Assert.Equal("Temperature", tableBlock.Table.Rows[0].CellsByHeader["Name"]);

        Assert.Equal("40002", tableBlock.Table.Rows[1].CellsByHeader["Address"]);
        Assert.Equal("Pressure", tableBlock.Table.Rows[1].CellsByHeader["Name"]);

        Assert.Equal("40003", tableBlock.Table.Rows[2].CellsByHeader["Address"]);
        Assert.Equal("Voltage", tableBlock.Table.Rows[2].CellsByHeader["Name"]);

        Assert.Equal("40004", tableBlock.Table.Rows[3].CellsByHeader["Address"]);
        Assert.Equal("Current", tableBlock.Table.Rows[3].CellsByHeader["Name"]);
    }

    [Fact]
    public async Task NormalizationVersion_ReingestionBehavior_UnchangedSkipsAndChangedReingests()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), FormattableString.Invariant($"pigmentor_normver_{Guid.NewGuid():N}.db"));
        var connStr = $"Data Source={tempDb}";

        try
        {
            using var vectorStore = new SqliteVecStore(new SqliteVecStoreOptions { ConnectionString = connStr, Dimensions = 2, EmbeddingModel = "test-model" });
            using var stateStore = new SqliteIngestionStateStore(connStr);
            var chunker = new RecursiveCharacterChunker(maxChunkSize: 500);
            var embedder = new TestEmbeddingService(2, "test-model");

            var meta = CreateMetadata("doc-version-test", "version_doc.txt");
            var doc = new Pigmentor.Core.Documents.Document(meta, "RS-485 Modbus RTU Technical Specification Content", "version_doc.txt");

            // 1. Initial ingestion with NormalizationVersion = 1
            var serviceV1 = new DocumentIngestionService(stateStore, vectorStore, chunker, embedder, normalizationVersion: 1);
            var res1 = await serviceV1.IngestAsync(doc);

            Assert.False(res1.Skipped);
            Assert.Equal(1, res1.Version);
            Assert.Equal(DocumentChangeKind.New, res1.ChangeKind);

            // 2. Second ingestion with same content and same NormalizationVersion = 1 -> unchanged skip!
            var res2 = await serviceV1.IngestAsync(doc);

            Assert.True(res2.Skipped);
            Assert.Equal(1, res2.Version);
            Assert.Equal(DocumentChangeKind.Unchanged, res2.ChangeKind);

            // 3. Third ingestion with same content but changed NormalizationVersion = 2 -> document reingests!
            var serviceV2 = new DocumentIngestionService(stateStore, vectorStore, chunker, embedder, normalizationVersion: 2);
            var res3 = await serviceV2.IngestAsync(doc);

            Assert.False(res3.Skipped);
            Assert.Equal(2, res3.Version);
            Assert.Equal(DocumentChangeKind.ContentChanged, res3.ChangeKind);
        }
        finally
        {
            if (File.Exists(tempDb))
            {
                try { File.Delete(tempDb); } catch { }
            }
        }
    }

    [Fact]
    public void TestPdfFixtures_EnsureFixturesCreatedOnDisk()
    {
        var testDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        TestPdfFixtures.EnsureFixturesCreated(testDir);

        var sourceFixturesDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Fixtures"));
        if (Directory.Exists(sourceFixturesDir))
        {
            TestPdfFixtures.EnsureFixturesCreated(sourceFixturesDir);
        }

        Assert.True(File.Exists(Path.Combine(testDir, "normal_text.pdf")));
        Assert.True(File.Exists(Path.Combine(testDir, "pdf_table.pdf")));
        Assert.True(File.Exists(Path.Combine(testDir, "multipage_table.pdf")));
        Assert.True(File.Exists(Path.Combine(testDir, "pdf_figure.pdf")));
        Assert.True(File.Exists(Path.Combine(testDir, "scanned_page.pdf")));
        Assert.True(File.Exists(Path.Combine(testDir, "scanned_table.pdf")));
        Assert.True(File.Exists(Path.Combine(testDir, "modbus_table.png")));

        Assert.True(new FileInfo(Path.Combine(testDir, "normal_text.pdf")).Length > 0);
        Assert.True(new FileInfo(Path.Combine(testDir, "pdf_table.pdf")).Length > 0);
        Assert.True(new FileInfo(Path.Combine(testDir, "multipage_table.pdf")).Length > 0);
        Assert.True(new FileInfo(Path.Combine(testDir, "pdf_figure.pdf")).Length > 0);
        Assert.True(new FileInfo(Path.Combine(testDir, "scanned_page.pdf")).Length > 0);
        Assert.True(new FileInfo(Path.Combine(testDir, "scanned_table.pdf")).Length > 0);
        Assert.True(new FileInfo(Path.Combine(testDir, "modbus_table.png")).Length > 0);
    }

    #endregion
}
