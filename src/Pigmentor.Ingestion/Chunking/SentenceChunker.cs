using System.Text;
using System.Text.RegularExpressions;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Chunking;

/// <summary>
/// Splits on sentence boundaries (. ! ?) and groups up to
/// <see cref="MaxSentencesPerChunk"/> consecutive sentences per chunk.
/// </summary>
public sealed partial class SentenceChunker : IChunker
{
    public int MaxSentencesPerChunk { get; }

    public SentenceChunker(int maxSentencesPerChunk = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSentencesPerChunk);
        MaxSentencesPerChunk = maxSentencesPerChunk;
    }

    public string Strategy => "sentence";

    [GeneratedRegex(@"(?<=[\.\!\?])\s+(?=[A-Z\d])", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceSplit();

    public async IAsyncEnumerable<DocumentChunk> ChunkAsync(
        Document document,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        await Task.Yield();

        var text = document.Content;
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        // Pre-compute sentence offsets so we can construct accurate Start/End ranges.
        var offsets = new List<(int Start, int End)>();
        int cursor = 0;
        foreach (var match in SentenceSplit().EnumerateMatches(text))
        {
            offsets.Add((cursor, match.Index));
            cursor = match.Index + match.Length;
        }
        if (cursor < text.Length)
        {
            offsets.Add((cursor, text.Length));
        }

        if (offsets.Count == 0)
        {
            yield break;
        }

        int index = 0;
        for (int i = 0; i < offsets.Count; i += MaxSentencesPerChunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = offsets[i];
            var last = offsets[Math.Min(i + MaxSentencesPerChunk - 1, offsets.Count - 1)];
            var slice = text[first.Start..last.End].Trim();
            if (slice.Length == 0)
            {
                continue;
            }

            yield return ChunkBuilder.Build(document, index++, first.Start, last.End, slice);
        }
    }
}
