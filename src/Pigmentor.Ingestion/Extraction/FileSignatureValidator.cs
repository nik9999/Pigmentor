using Pigmentor.Core.Abstractions;

namespace Pigmentor.Ingestion.Extraction;

/// <summary>
/// Defensive file validation verifying stream readability, size limits, and binary signatures.
/// </summary>
public static class FileSignatureValidator
{
    private static readonly byte[] PdfMagic = [0x25, 0x50, 0x44, 0x46]; // %PDF
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04]; // PK\x03\x04 (docx, xlsx)
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] BmpMagic = [0x42, 0x4D]; // BM

    public static void Validate(Stream stream, string filePath, ExtractionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        limits ??= ExtractionLimits.Default;

        if (!stream.CanRead)
        {
            throw new InvalidOperationException($"Stream for file '{filePath}' is not readable.");
        }

        if (stream.CanSeek)
        {
            if (stream.Length == 0)
            {
                throw new InvalidDataException($"File '{filePath}' is empty (0 bytes).");
            }

            if (stream.Length > limits.MaxFileSizeBytes)
            {
                throw new InvalidOperationException(
                    $"File '{filePath}' size ({stream.Length} bytes) exceeds the maximum allowed limit of {limits.MaxFileSizeBytes} bytes.");
            }
        }

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext))
        {
            throw new NotSupportedException($"File '{filePath}' does not have a valid extension.");
        }

        if (stream.CanSeek)
        {
            var originalPosition = stream.Position;
            try
            {
                var header = new byte[8];
                var bytesRead = stream.Read(header, 0, header.Length);
                stream.Position = originalPosition;

                if (bytesRead < 2 && ext != ".txt")
                {
                    throw new InvalidDataException($"File '{filePath}' is too short to contain a valid {ext} header.");
                }

                switch (ext)
                {
                    case ".pdf":
                        if (bytesRead < 4 || !StartsWith(header, PdfMagic))
                        {
                            throw new InvalidDataException($"File '{filePath}' does not contain a valid PDF signature (%PDF).");
                        }
                        break;

                    case ".docx":
                    case ".xlsx":
                        if (bytesRead < 4 || !StartsWith(header, ZipMagic))
                        {
                            throw new InvalidDataException($"File '{filePath}' does not contain a valid OpenXML ZIP signature (PK).");
                        }
                        break;

                    case ".png":
                        if (bytesRead < 8 || !StartsWith(header, PngMagic))
                        {
                            throw new InvalidDataException($"File '{filePath}' does not contain a valid PNG signature.");
                        }
                        break;

                    case ".jpg":
                    case ".jpeg":
                        if (bytesRead < 3 || !StartsWith(header, JpegMagic))
                        {
                            throw new InvalidDataException($"File '{filePath}' does not contain a valid JPEG signature.");
                        }
                        break;

                    case ".bmp":
                        if (bytesRead < 2 || !StartsWith(header, BmpMagic))
                        {
                            throw new InvalidDataException($"File '{filePath}' does not contain a valid BMP signature (BM).");
                        }
                        break;

                    case ".txt":
                        // Any stream is valid for txt
                        break;
                }
            }
            finally
            {
                stream.Position = originalPosition;
            }
        }
    }

    private static bool StartsWith(byte[] array, byte[] prefix)
    {
        if (array.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (array[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }
}

