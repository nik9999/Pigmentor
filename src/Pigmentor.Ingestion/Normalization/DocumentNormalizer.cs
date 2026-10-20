using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Ingestion.Fingerprints;
using Pigmentor.Ingestion.Normalization.Rules;

namespace Pigmentor.Ingestion.Normalization;

/// <summary>
/// Production document normalization pipeline implementing deterministic Unicode cleaning,
/// conservative dehyphenation, layout noise removal, empty block pruning, and deduplication.
/// </summary>
public sealed class DocumentNormalizer : IDocumentNormalizer
{
    public int NormalizationVersion { get; } = IngestionFingerprint.DefaultNormalizationVersion;

    public Task<NormalizedDocument> NormalizeAsync(
        ExtractedDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var diagnostics = new NormalizationDiagnostics();

        // 1. Count extracted pages
        var detectedPages = document.Blocks
            .Where(b => b.Provenance.PageNumber.HasValue)
            .Select(b => b.Provenance.PageNumber!.Value)
            .Distinct()
            .Count();

        diagnostics.ExtractedPages = Math.Max(1, detectedPages);

        cancellationToken.ThrowIfCancellationRequested();

        // 2. Unicode normalization (FormC, control chars, zero-width chars, soft-hyphen, NBSP, technical symbols preserved)
        var blocks = document.Blocks
            .Select(UnicodeNormalizationRule.Normalize)
            .ToList();

        cancellationToken.ThrowIfCancellationRequested();

        // 3. Repeated headers / footers removal (PDF-specific multi-page document-wide detection)
        if (string.Equals(document.Format, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            blocks = HeaderFooterRemovalRule.Normalize(blocks, diagnostics).ToList();
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 4. Page number noise removal from searchable prose (provenance preserved)
        blocks = blocks
            .Select(b => PageNumberNoiseRule.Normalize(b, diagnostics))
            .ToList();

        cancellationToken.ThrowIfCancellationRequested();

        // 5. Conservative dehyphenation (repairing word splits, preserving RS-485, 4-20 mA, Modbus-TCP)
        blocks = blocks
            .Select(b => DehyphenationRule.Normalize(b, diagnostics))
            .ToList();

        cancellationToken.ThrowIfCancellationRequested();

        // 6. Paragraph reconstruction (merging layout line-wrapping, preserving paragraph breaks; tables/code exempt)
        blocks = blocks
            .Select(ParagraphReconstructionRule.Normalize)
            .ToList();

        cancellationToken.ThrowIfCancellationRequested();

        // 7. Prune empty blocks & count dropped empty pages
        blocks = EmptyBlockPruningRule.Prune(blocks, diagnostics.ExtractedPages, diagnostics).ToList();

        cancellationToken.ThrowIfCancellationRequested();

        // 8. Deduplicate exact duplicate blocks / pages
        blocks = DeduplicationRule.Deduplicate(blocks, diagnostics).ToList();

        // 9. Count structural tables & figures
        diagnostics.TablesExtracted = blocks.Count(b => b is TableBlock);
        diagnostics.FiguresExtracted = blocks.Count(b => b is FigureBlock);

        var normalizedDoc = new NormalizedDocument(
            DocumentId: document.DocumentId,
            SourcePath: document.SourcePath,
            Format: document.Format,
            Blocks: blocks,
            Metadata: document.Metadata,
            Diagnostics: diagnostics,
            NormalizationVersion: NormalizationVersion);

        return Task.FromResult(normalizedDoc);
    }
}

