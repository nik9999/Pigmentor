namespace Pigmentor.Core.Abstractions;

/// <summary>
/// Result of an OCR recognition operation on an image stream.
/// </summary>
public sealed record OcrResult(
    string Text,
    double Confidence = 1.0,
    IReadOnlyList<string>? DetectedRegions = null,
    IReadOnlyList<OcrLine>? Lines = null)
{
    /// <summary>Empty OCR result indicating no recognized text.</summary>
    public static OcrResult Empty { get; } = new(string.Empty, 0.0);
}

/// <summary>
/// A single line of text recognized by an OCR engine with optional bounding box and word-level layout elements.
/// </summary>
public sealed record OcrLine(
    string Text,
    IReadOnlyList<OcrWord>? Words = null,
    OcrBoundingBox? Bounds = null,
    double Confidence = 1.0);

/// <summary>
/// An individual word recognized within an OCR line with layout bounds.
/// </summary>
public sealed record OcrWord(
    string Text,
    OcrBoundingBox Bounds,
    double Confidence = 1.0);

/// <summary>
/// Geometric rectangular bounding box for an OCR element in points or pixels.
/// </summary>
public readonly record struct OcrBoundingBox(
    double Left,
    double Top,
    double Width,
    double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
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

