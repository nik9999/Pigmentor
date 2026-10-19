using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Chunking;

/// <summary>
/// Splits the document text into fixed-character chunks with optional
/// overlap. Useful as the baseline for chunking comparisons (Ch 4).
/// </summary>
public sealed class FixedSizeChunker : IChunker
{
    private readonly int _chunkSize;
    private readonly int _overlap;

    public FixedSizeChunker(int chunkSize = 500, int overlap = 100)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        ArgumentOutOfRangeException.ThrowIfNegative(overlap);
        if (overlap >= chunkSize)
        {
            throw new ArgumentOutOfRangeException(nameof(overlap), "overlap must be smaller than chunkSize");
        }
        _chunkSize = chunkSize;
        _overlap = overlap;
    }

    public string Strategy => "fixed-size";

    public async IAsyncEnumerable<DocumentChunk> ChunkAsync(
        Document document,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        await Task.Yield();

        var text = document.Content;
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        int index = 0;
        int start = 0;
        while (start < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = Math.Min(start + _chunkSize, text.Length);
            yield return ChunkBuilder.Build(document, index, start, end, text[start..end]);
            index++;
            if (end == text.Length)
            {
                break;
            }

            start = end - _overlap;
        }
    }
}
