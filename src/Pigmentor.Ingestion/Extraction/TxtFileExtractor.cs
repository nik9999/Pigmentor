using System.Globalization;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Extraction;

/// <summary>
/// Extracts structural text blocks from plain text (<c>.txt</c>) files.
/// </summary>
public sealed class TxtFileExtractor : IFileExtractor
{
    private readonly ExtractionLimits _limits;

    public TxtFileExtractor(ExtractionLimits? limits = null)
    {
        _limits = limits ?? ExtractionLimits.Default;
    }

    public IReadOnlySet<string> SupportedExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".txt"
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

        using var reader = new StreamReader(stream, leaveOpen: true);
        var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        var paragraphs = content
            .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var blocks = new List<ExtractedBlock>(paragraphs.Length);
        for (var i = 0; i < paragraphs.Length; i++)
        {
            var text = paragraphs[i].Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var prov = new BlockProvenance(metadata.Id, PageNumber: 1);
            blocks.Add(new TextBlock(FormattableString.Invariant($"{metadata.Id}#b{i}"), text, prov));
        }

        return new ExtractedDocument(metadata.Id, filePath, ".txt", blocks, metadata);
    }
}
