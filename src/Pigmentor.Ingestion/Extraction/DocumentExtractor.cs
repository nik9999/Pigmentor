using System.Globalization;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Extraction;

/// <summary>
/// Composite document extractor that delegates extraction to format-specific extractors based on file extension.
/// </summary>
public sealed class DocumentExtractor : IDocumentExtractor
{
    private readonly Dictionary<string, IFileExtractor> _extractors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ExtractionLimits _limits;

    public DocumentExtractor(
        IEnumerable<IFileExtractor>? extractors = null,
        IOcrEngine? ocrEngine = null,
        IOcrTableDetector? ocrTableDetector = null,
        ExtractionLimits? limits = null)
    {
        _limits = limits ?? ExtractionLimits.Default;

        if (extractors != null)
        {
            foreach (var extractor in extractors)
            {
                RegisterExtractor(extractor);
            }
        }
        else
        {
            // Default built-in extractors
            RegisterExtractor(new TxtFileExtractor(_limits));
            RegisterExtractor(new XlsxFileExtractor(_limits));
            RegisterExtractor(new DocxFileExtractor(_limits));
            RegisterExtractor(new ImageFileExtractor(ocrEngine, ocrTableDetector, _limits));
            RegisterExtractor(new PdfFileExtractor(ocrEngine, ocrTableDetector, _limits));
        }
    }

    public void RegisterExtractor(IFileExtractor extractor)
    {
        ArgumentNullException.ThrowIfNull(extractor);
        foreach (var ext in extractor.SupportedExtensions)
        {
            _extractors[ext] = extractor;
        }
    }

    public bool CanExtract(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        var normalizedExt = extension.StartsWith('.') ? extension : "." + extension;
        return _extractors.ContainsKey(normalizedExt);
    }

    public async Task<ExtractedDocument> ExtractAsync(
        Stream stream,
        string filePath,
        DocumentMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(metadata);

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (!_extractors.TryGetValue(ext, out var extractor))
        {
            throw new NotSupportedException(
                $"File format '{ext}' is not supported by Pigmentor v1 document extraction. Supported formats are: {string.Join(", ", _extractors.Keys)}.");
        }

        return await extractor.ExtractAsync(stream, filePath, metadata, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExtractedDocument> ExtractFileAsync(
        string filePath,
        DocumentMetadata? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Source document file not found: '{filePath}'", filePath);
        }

        var fileName = Path.GetFileName(filePath);
        var docId = metadata?.Id ?? Path.GetFileNameWithoutExtension(filePath);

        metadata ??= new DocumentMetadata(
            Id: docId,
            Silo: "technical-docs",
            Department: "Engineering",
            Office: "Main",
            ConfidentialityLevel: "Internal",
            DocumentType: "Manual",
            FiscalYear: DateTime.UtcNow.Year,
            Author: "System",
            LastModified: DateOnly.FromDateTime(File.GetLastWriteTimeUtc(filePath)),
            Title: Path.GetFileNameWithoutExtension(filePath));

        await using var stream = File.OpenRead(filePath);
        return await ExtractAsync(stream, filePath, metadata, cancellationToken).ConfigureAwait(false);
    }
}
