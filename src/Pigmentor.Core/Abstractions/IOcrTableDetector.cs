using System.Diagnostics.CodeAnalysis;
using Pigmentor.Core.Documents;

namespace Pigmentor.Core.Abstractions;

/// <summary>
/// Strategy for analyzing OCR results and detecting structured tabular layout.
/// Used to route scanned tables into first-class <see cref="ExtractedTable"/> and <see cref="TableBlock"/> structures.
/// </summary>
public interface IOcrTableDetector
{
    /// <summary>
    /// Attempts to detect and reconstruct a structured table from an OCR result.
    /// Returns <see langword="true"/> if a structured table was confidently detected; otherwise <see langword="false"/>.
    /// </summary>
    /// <param name="ocrResult">The OCR result to analyze.</param>
    /// <param name="documentId">The document identifier for table ID generation.</param>
    /// <param name="pageNumber">The source page number.</param>
    /// <param name="defaultCaption">Optional default caption if no in-text caption is identified.</param>
    /// <param name="table">When this method returns <see langword="true"/>, contains the reconstructed table; otherwise <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> if a valid table is detected; otherwise <see langword="false"/>.</returns>
    bool TryDetectTable(
        OcrResult ocrResult,
        string documentId,
        int pageNumber,
        string? defaultCaption,
        [NotNullWhen(true)] out ExtractedTable? table,
        CancellationToken cancellationToken = default);
}

