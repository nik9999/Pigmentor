using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Normalization.Rules;

/// <summary>
/// Prunes empty or pure whitespace/noise blocks and records dropped empty pages in diagnostics.
/// </summary>
public static class EmptyBlockPruningRule
{
    public static IReadOnlyList<ExtractedBlock> Prune(
        IReadOnlyList<ExtractedBlock> blocks,
        int totalExtractedPages,
        NormalizationDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var validBlocks = new List<ExtractedBlock>(blocks.Count);
        var pagesWithContent = new HashSet<int>();

        foreach (var block in blocks)
        {
            var isMeaningful = block switch
            {
                TableBlock tb => tb.Table.Rows.Count > 0,
                FigureBlock fb => !string.IsNullOrWhiteSpace(fb.Caption) || !string.IsNullOrWhiteSpace(fb.OcrText),
                _ => !string.IsNullOrWhiteSpace(block.Text)
            };

            if (isMeaningful)
            {
                validBlocks.Add(block);
                if (block.Provenance.PageNumber.HasValue)
                {
                    pagesWithContent.Add(block.Provenance.PageNumber.Value);
                }
            }
            else
            {
                diagnostics.DroppedEmptyBlocks++;
            }
        }

        if (totalExtractedPages > 0)
        {
            var emptyPages = totalExtractedPages - pagesWithContent.Count;
            if (emptyPages > 0)
            {
                diagnostics.DroppedEmptyPages += emptyPages;
            }
        }

        return validBlocks;
    }
}

