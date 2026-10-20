using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Pigmentor.Tests.Fixtures;

/// <summary>
/// Deterministic test fixtures generator and helper for PDF documents and images.
/// Generates small, self-contained test assets for tables, multi-page tables, figures with context, and scans.
/// </summary>
public static class TestPdfFixtures
{
    public static readonly byte[] MinimalValidJpeg =
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x01, 0x00, 0x48,
        0x00, 0x48, 0x00, 0x00, 0xFF, 0xDB, 0x00, 0x43, 0x00, 0x08, 0x06, 0x06, 0x07, 0x06, 0x05, 0x08,
        0x07, 0x07, 0x07, 0x09, 0x09, 0x08, 0x0A, 0x0C, 0x14, 0x0D, 0x0C, 0x0B, 0x0B, 0x0C, 0x19, 0x12,
        0x13, 0x0F, 0x14, 0x1D, 0x1A, 0x1F, 0x1E, 0x1D, 0x1A, 0x1C, 0x1C, 0x20, 0x24, 0x2E, 0x27, 0x20,
        0x22, 0x2C, 0x23, 0x1C, 0x1C, 0x28, 0x37, 0x29, 0x2C, 0x30, 0x31, 0x34, 0x34, 0x34, 0x1F, 0x27,
        0x39, 0x3D, 0x38, 0x32, 0x3C, 0x2E, 0x33, 0x34, 0x32, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x01,
        0x00, 0x01, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xC4, 0x00, 0x1F, 0x00, 0x00, 0x01, 0x05, 0x01, 0x01,
        0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04,
        0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F,
        0x00, 0xBF, 0x80, 0xFF, 0xD9
    ];

    public static readonly byte[] MinimalValidPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82
    ];

    public static byte[] CreateNormalTextPdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        page.AddText("Pigmentor System Overview", 14, new PdfPoint(50, 750), font);
        page.AddText("Pigmentor provides deterministic ingestion for industrial technical documentation.", 11, new PdfPoint(50, 710), font);
        page.AddText("RS-485 and Modbus RTU communication protocols are fully supported.", 11, new PdfPoint(50, 690), font);

        return builder.Build();
    }

    public static byte[] CreatePdfTablePdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        page.AddText("Table 1 - Device Modbus Registers", 12, new PdfPoint(50, 750), font);
        page.AddText("Address | Name | Type | Access", 12, new PdfPoint(50, 720), font);
        page.AddText("40001 | Temperature | INT16 | RO", 12, new PdfPoint(50, 700), font);
        page.AddText("40002 | Pressure | UINT16 | RO", 12, new PdfPoint(50, 680), font);
        page.AddText("40003 | Humidity | INT16 | RO", 12, new PdfPoint(50, 660), font);

        return builder.Build();
    }

    public static byte[] CreateMultiPageTablePdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        // Page 1: Initial table
        var page1 = builder.AddPage(PageSize.A4);
        page1.AddText("Table 1 - Device Registers", 12, new PdfPoint(50, 750), font);
        page1.AddText("Address | Name | Type | Access", 12, new PdfPoint(50, 720), font);
        page1.AddText("40001 | Temperature | INT16 | RO", 12, new PdfPoint(50, 700), font);
        page1.AddText("40002 | Pressure | UINT16 | RO", 12, new PdfPoint(50, 680), font);

        // Page 2: Continuation with repeated header
        var page2 = builder.AddPage(PageSize.A4);
        page2.AddText("Table 1 - Continued", 12, new PdfPoint(50, 750), font);
        page2.AddText("Address | Name | Type | Access", 12, new PdfPoint(50, 720), font);
        page2.AddText("40003 | Voltage | FLOAT32 | RO", 12, new PdfPoint(50, 700), font);
        page2.AddText("40004 | Current | FLOAT32 | RO", 12, new PdfPoint(50, 680), font);

        return builder.Build();
    }

    public static byte[] CreatePdfFigurePdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        // Meaningful paragraph before figure (Y ~ 650-670)
        page.AddText("Connection of RS-485 interface uses a two-wire line.", 11, new PdfPoint(50, 650), font);

        // Embedded figure image (Y 400 to 600, height 200 > 45, width 200 > 45)
        page.AddJpeg(MinimalValidJpeg, new PdfRectangle(50, 400, 250, 600));

        // Caption nearby figure (Y ~ 370)
        page.AddText("Figure 12 - Modbus RTU Wiring Schematic", 11, new PdfPoint(50, 370), font);

        // Meaningful paragraph after figure (Y ~ 330)
        page.AddText("Install a 120 Ohm termination resistor at the end of the line.", 11, new PdfPoint(50, 330), font);

        // Page number noise at bottom (Y ~ 30)
        page.AddText("12", 10, new PdfPoint(280, 30), font);

        return builder.Build();
    }

    public static byte[] CreateScannedPagePdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);

        // Dominant full-page raster image covering > 50% of page area, 0 words
        page.AddJpeg(MinimalValidJpeg, new PdfRectangle(20, 20, 570, 820));

        return builder.Build();
    }

    public static byte[] CreateScannedTablePdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);

        // Dominant full-page raster image covering > 50% of page area, 0 words
        page.AddJpeg(MinimalValidJpeg, new PdfRectangle(20, 20, 570, 820));

        return builder.Build();
    }

    public static void EnsureFixturesCreated(string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);

        File.WriteAllBytes(Path.Combine(directoryPath, "normal_text.pdf"), CreateNormalTextPdf());
        File.WriteAllBytes(Path.Combine(directoryPath, "pdf_table.pdf"), CreatePdfTablePdf());
        File.WriteAllBytes(Path.Combine(directoryPath, "multipage_table.pdf"), CreateMultiPageTablePdf());
        File.WriteAllBytes(Path.Combine(directoryPath, "pdf_figure.pdf"), CreatePdfFigurePdf());
        File.WriteAllBytes(Path.Combine(directoryPath, "scanned_page.pdf"), CreateScannedPagePdf());
        File.WriteAllBytes(Path.Combine(directoryPath, "scanned_table.pdf"), CreateScannedTablePdf());
        File.WriteAllBytes(Path.Combine(directoryPath, "modbus_table.png"), MinimalValidPng);
    }
}

