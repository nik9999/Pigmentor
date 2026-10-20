using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Pigmentor.Tests.Fixtures;

/// <summary>
/// Helper generator for creating temporary multi-format test documents (.txt, .docx, .xlsx, .pdf, .png).
/// </summary>
public static class MultiFormatFixtureFiles
{
    public static string CreateTxt(string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    public static string CreateDocx(
        string directory,
        string fileName,
        string title,
        string paragraphText,
        (string[] Headers, string[][] Rows)? table = null)
    {
        var path = Path.Combine(directory, fileName);
        using (var wordDoc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document))
        {
            var mainPart = wordDoc.AddMainDocumentPart();
            mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document();
            var body = new Body();

            if (!string.IsNullOrEmpty(title))
            {
                var pTitle = new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                    new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(title)));
                body.Append(pTitle);
            }

            if (!string.IsNullOrEmpty(paragraphText))
            {
                var p = new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                    new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(paragraphText)));
                body.Append(p);
            }

            if (table.HasValue)
            {
                var docxTable = new DocumentFormat.OpenXml.Wordprocessing.Table();

                var headerRow = new DocumentFormat.OpenXml.Wordprocessing.TableRow();
                foreach (var h in table.Value.Headers)
                {
                    headerRow.Append(new DocumentFormat.OpenXml.Wordprocessing.TableCell(
                        new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                            new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(h)))));
                }
                docxTable.Append(headerRow);

                foreach (var r in table.Value.Rows)
                {
                    var dataRow = new DocumentFormat.OpenXml.Wordprocessing.TableRow();
                    foreach (var c in r)
                    {
                        dataRow.Append(new DocumentFormat.OpenXml.Wordprocessing.TableCell(
                            new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                                new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(c)))));
                    }
                    docxTable.Append(dataRow);
                }

                body.Append(docxTable);
            }

            mainPart.Document.Append(body);
            mainPart.Document.Save();
        }
        return path;
    }

    public static string CreateXlsx(
        string directory,
        string fileName,
        string sheetName,
        string[] headers,
        string[][] rows)
    {
        var path = Path.Combine(directory, fileName);
        using (var spreadsheet = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook))
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

            var headerRow = new Row();
            foreach (var h in headers)
            {
                headerRow.Append(new Cell { DataType = CellValues.String, CellValue = new CellValue(h) });
            }
            sheetData.Append(headerRow);

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
        return path;
    }

    public static string CreatePdf(
        string directory,
        string fileName,
        string title,
        IReadOnlyList<string> lines)
    {
        var path = Path.Combine(directory, fileName);
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        page.AddText(title, 12, new PdfPoint(50, 750), font);
        var y = 720;
        foreach (var line in lines)
        {
            page.AddText(line, 10, new PdfPoint(50, y), font);
            y -= 20;
        }

        var bytes = builder.Build();
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public static string CreateImage(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        // Valid 1x1 PNG bytes
        byte[] pngBytes =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
            0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
            0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
            0x42, 0x60, 0x82
        ];
        File.WriteAllBytes(path, pngBytes);
        return path;
    }
}
