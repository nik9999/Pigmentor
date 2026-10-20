using System.Text;

namespace Pigmentor.Core.Documents;

/// <summary>
/// Common base record for all structural blocks extracted from source documents.
/// </summary>
public abstract record ExtractedBlock(
    string BlockId,
    BlockKind Kind,
    string Text,
    BlockProvenance Provenance);

/// <summary>
/// A prose body paragraph or textual section.
/// </summary>
public sealed record TextBlock(
    string BlockId,
    string Text,
    BlockProvenance Provenance) : ExtractedBlock(BlockId, BlockKind.Text, Text, Provenance);

/// <summary>
/// A document or section heading.
/// </summary>
public sealed record HeadingBlock(
    string BlockId,
    string Text,
    int Level,
    BlockProvenance Provenance) : ExtractedBlock(BlockId, BlockKind.Heading, Text, Provenance);

/// <summary>
/// A first-class structured table block.
/// </summary>
public sealed record TableBlock(
    string BlockId,
    ExtractedTable Table,
    BlockProvenance Provenance) : ExtractedBlock(BlockId, BlockKind.Table, Table.ToMarkdownTable(), Provenance);

/// <summary>
/// An embedded graphic, schematic, or diagram block with caption and OCR-recognized text.
/// </summary>
public sealed record FigureBlock(
    string BlockId,
    string FigureId,
    string Caption,
    string OcrText,
    string SurroundingContext,
    string? AssetReference,
    BlockProvenance Provenance) : ExtractedBlock(BlockId, BlockKind.Figure, BuildFigureSearchableText(Caption, SurroundingContext, OcrText), Provenance)
{
    private static string BuildFigureSearchableText(string caption, string context, string ocr)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(caption))
        {
            sb.AppendLine(caption);
        }

        if (!string.IsNullOrWhiteSpace(context))
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }
            sb.AppendLine("Контекст:").AppendLine(context);
        }

        if (!string.IsNullOrWhiteSpace(ocr))
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }
            sb.AppendLine("Текст на рисунке:").AppendLine(ocr);
        }

        return sb.ToString().Trim();
    }
}

/// <summary>
/// Preformatted code, configuration, register dump, or machine-readable listing.
/// </summary>
public sealed record CodeBlock(
    string BlockId,
    string Text,
    string? Language,
    BlockProvenance Provenance) : ExtractedBlock(BlockId, BlockKind.Code, Text, Provenance);

/// <summary>
/// Bullet or numbered list item.
/// </summary>
public sealed record ListBlock(
    string BlockId,
    string Text,
    bool IsOrdered,
    BlockProvenance Provenance) : ExtractedBlock(BlockId, BlockKind.List, Text, Provenance);

