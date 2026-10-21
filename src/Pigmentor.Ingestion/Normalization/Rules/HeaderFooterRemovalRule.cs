using System.Text.RegularExpressions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Normalization.Rules;

/// <summary>
/// Detects and strips repeated running headers and footers across pages in paginated documents.
/// </summary>
public static partial class HeaderFooterRemovalRule
{
    private const int MinDocumentPages = 3;
    private const double OccurrenceThreshold = 0.65;
    private const int MaxHeaderFooterLength = 160;

    [GeneratedRegex(@"\b\d+\b")]
    private static partial Regex DigitsRegex();

    public static IReadOnlyList<ExtractedBlock> Normalize(
        IReadOnlyList<ExtractedBlock> blocks,
        NormalizationDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (blocks.Count == 0)
        {
            return blocks;
        }

        // Group blocks by page
        var pageGroups = blocks
            .Where(b => b.Provenance.PageNumber.HasValue)
            .GroupBy(b => b.Provenance.PageNumber!.Value)
            .OrderBy(g => g.Key)
            .ToList();

        if (pageGroups.Count < MinDocumentPages)
        {
            return blocks;
        }

        var totalPages = pageGroups.Count;

        // Detect running headers (first line of first text block on page)
        var firstLines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var firstTemplates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Detect running footers (last line of last text block on page)
        var lastLines = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lastTemplates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var page in pageGroups)
        {
            var textBlocks = page.Where(b => b is TextBlock or HeadingBlock).ToList();
            if (textBlocks.Count == 0)
            {
                continue;
            }

            var firstBlockLines = GetLines(textBlocks[0].Text);
            if (firstBlockLines.Count > 0)
            {
                var fl = firstBlockLines[0].Trim();
                if (fl.Length is > 3 and <= MaxHeaderFooterLength)
                {
                    firstLines[fl] = firstLines.GetValueOrDefault(fl) + 1;
                    var tmpl = NormalizeTemplate(fl);
                    if (tmpl.Length > 5)
                    {
                        firstTemplates[tmpl] = firstTemplates.GetValueOrDefault(tmpl) + 1;
                    }
                }
            }

            var lastBlockLines = GetLines(textBlocks[^1].Text);
            if (lastBlockLines.Count > 0)
            {
                var ll = lastBlockLines[^1].Trim();
                if (ll.Length is > 3 and <= MaxHeaderFooterLength)
                {
                    lastLines[ll] = lastLines.GetValueOrDefault(ll) + 1;
                    var tmpl = NormalizeTemplate(ll);
                    if (tmpl.Length > 5)
                    {
                        lastTemplates[tmpl] = lastTemplates.GetValueOrDefault(tmpl) + 1;
                    }
                }
            }
        }

        var repeatedHeaders = firstLines
            .Where(kvp => (double)kvp.Value / totalPages >= OccurrenceThreshold)
            .Select(kvp => kvp.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var repeatedHeaderTemplates = firstTemplates
            .Where(kvp => (double)kvp.Value / totalPages >= OccurrenceThreshold)
            .Select(kvp => kvp.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var repeatedFooters = lastLines
            .Where(kvp => (double)kvp.Value / totalPages >= OccurrenceThreshold)
            .Select(kvp => kvp.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var repeatedFooterTemplates = lastTemplates
            .Where(kvp => (double)kvp.Value / totalPages >= OccurrenceThreshold)
            .Select(kvp => kvp.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (repeatedHeaders.Count == 0 && repeatedFooters.Count == 0 &&
            repeatedHeaderTemplates.Count == 0 && repeatedFooterTemplates.Count == 0)
        {
            return blocks;
        }

        var result = new List<ExtractedBlock>(blocks.Count);

        foreach (var block in blocks)
        {
            if (block is not TextBlock and not HeadingBlock)
            {
                result.Add(block);
                continue;
            }

            var lines = GetLines(block.Text);
            var modified = false;

            if (lines.Count > 0)
            {
                var fl = lines[0].Trim();
                if (repeatedHeaders.Contains(fl) || repeatedHeaderTemplates.Contains(NormalizeTemplate(fl)))
                {
                    lines.RemoveAt(0);
                    diagnostics.RemovedHeaders++;
                    modified = true;
                }
            }

            if (lines.Count > 0)
            {
                var ll = lines[^1].Trim();
                if (repeatedFooters.Contains(ll) || repeatedFooterTemplates.Contains(NormalizeTemplate(ll)))
                {
                    lines.RemoveAt(lines.Count - 1);
                    diagnostics.RemovedFooters++;
                    modified = true;
                }
            }

            if (modified)
            {
                var newText = string.Join("\n", lines).Trim();
                result.Add(block switch
                {
                    TextBlock tb => tb with { Text = newText },
                    HeadingBlock hb => hb with { Text = newText },
                    _ => block
                });
            }
            else
            {
                result.Add(block);
            }
        }

        return result;
    }

    private static string NormalizeTemplate(string line) => DigitsRegex().Replace(line, "").Trim();

    private static List<string> GetLines(string text)
    {
        return text
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();
    }
}

