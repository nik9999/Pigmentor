using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Multimodal;

/// <summary>Extracts embedded images from a PDF document for downstream captioning.</summary>
public interface IPdfImageExtractor
{
    /// <summary>The implementation name for OpenTelemetry attributes (e.g. <c>pdfpig</c>, <c>azure-doc-intel</c>).</summary>
    string Implementation { get; }

    /// <summary>
    /// Extract every embedded raster image from <paramref name="pdfPath"/>.
    /// Each entry's <c>ImagePath</c> is a path under <paramref name="outputDirectory"/>
    /// where the bytes were written; <c>Caption</c> is empty until
    /// <see cref="ImageCaptioner"/> processes it.
    /// </summary>
    Task<IReadOnlyList<ExtractedImage>> ExtractAsync(
        string pdfPath,
        string outputDirectory,
        string documentId,
        CancellationToken cancellationToken = default);
}
