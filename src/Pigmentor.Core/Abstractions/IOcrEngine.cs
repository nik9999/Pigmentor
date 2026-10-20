namespace Pigmentor.Core.Abstractions;

/// <summary>
/// Result of an OCR recognition operation on an image stream.
/// </summary>
public sealed record OcrResult(
    string Text,
    double Confidence = 1.0,
    IReadOnlyList<string>? DetectedRegions = null)
{
    /// <summary>Empty OCR result indicating no recognized text.</summary>
    public static OcrResult Empty { get; } = new(string.Empty, 0.0);
}

/// <summary>
/// Pluggable OCR engine abstraction for extracting text from images and scanned PDF pages.
/// </summary>
public interface IOcrEngine
{
    /// <summary>
    /// Performs text recognition on the provided image stream.
    /// </summary>
    Task<OcrResult> RecognizeAsync(
        Stream imageStream,
        CancellationToken cancellationToken = default);
}

