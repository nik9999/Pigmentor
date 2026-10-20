using System.Globalization;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Ingestion.Quality;

namespace Pigmentor.Ingestion.Extraction;

/// <summary>
/// Unified image extractor for BMP, PNG, JPG, and JPEG files using an abstracted <see cref="IOcrEngine"/>.
/// </summary>
public sealed class ImageFileExtractor : IFileExtractor
{
    private readonly IOcrEngine? _ocrEngine;
    private readonly ExtractionLimits _limits;

    public ImageFileExtractor(IOcrEngine? ocrEngine = null, ExtractionLimits? limits = null)
    {
        _ocrEngine = ocrEngine;
        _limits = limits ?? ExtractionLimits.Default;
    }

    public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp",
        ".png",
        ".jpg",
        ".jpeg"
    };

    public async Task<ExtractedDocument> ExtractAsync(
        Stream stream,
        string filePath,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(metadata);

        FileSignatureValidator.Validate(stream, filePath, _limits);

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (_ocrEngine == null)
        {
            throw new InvalidOperationException(
                $"Cannot extract text from image '{filePath}' because no IOcrEngine is configured. Register an IOcrEngine to enable image ingestion.");
        }

        var ocrResult = await _ocrEngine.RecognizeAsync(stream, cancellationToken).ConfigureAwait(false);
        var blocks = new List<ExtractedBlock>();

        if (!string.IsNullOrWhiteSpace(ocrResult?.Text))
        {
            var assessment = OcrQualityGate.Assess(ocrResult.Text);
            if (assessment.IsAcceptable)
            {
                var prov = new BlockProvenance(metadata.Id, PageNumber: 1, AssetReference: Path.GetFileName(filePath));
                blocks.Add(new TextBlock(FormattableString.Invariant($"{metadata.Id}#img-0"), ocrResult.Text.Trim(), prov));
            }
        }

        return new ExtractedDocument(metadata.Id, filePath, ext, blocks, metadata);
    }
}

