using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Chunking;

/// <summary>
/// Splits text by walking a hierarchy of separators ("\n\n", "\n", ". ", " ")
/// — markdown-aware: section boundaries first, paragraphs second, sentences
/// third, words last. Descends to the next separator only for pieces that still
/// exceed <see cref="MaxChunkSize"/>.
/// </summary>
/// <remarks>
/// Pieces are never re-merged: a short section stays its own chunk even when it
/// would fit alongside the next one, so a 200-character budget over a document
/// of small sections yields several well-under-budget chunks. Whitespace-only
/// pieces are dropped rather than emitted as empty chunks.
/// </remarks>
public sealed class RecursiveCharacterChunker : IChunker
{
    public int MaxChunkSize { get; }
    public IReadOnlyList<string> Separators { get; }

    public RecursiveCharacterChunker(int maxChunkSize = 800, IReadOnlyList<string>? separators = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChunkSize);
        MaxChunkSize = maxChunkSize;
        Separators = separators ?? ["\n## ", "\n# ", "\n\n", "\n", ". ", " "];
    }

    public string Strategy => "recursive";

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
        foreach (var (start, end) in Split(text, 0, text.Length, separatorIndex: 0))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Split only guarantees a non-zero RAW span, so a piece made up of
            // separator characters ("\n\n", "   ") survives it and then trims to
            // nothing. Emitting that produces an empty chunk, which downstream
            // assumes cannot happen: EmbeddingService documents non-blank input,
            // and an empty string embeds to a meaningless vector that still
            // occupies a row and can be returned by a search.
            // SentenceChunker has always had this guard; this one did not.
            var body = text[start..end].Trim();
            if (body.Length == 0)
            {
                continue;
            }

            yield return ChunkBuilder.Build(document, index++, start, end, body);
        }
    }

    private IEnumerable<(int Start, int End)> Split(string text, int start, int end, int separatorIndex)
    {
        var len = end - start;
        if (len <= MaxChunkSize)
        {
            if (len > 0)
            {
                yield return (start, end);
            }

            yield break;
        }
        if (separatorIndex >= Separators.Count)
        {
            // Hard-cut: walk forward in MaxChunkSize-sized windows.
            int cursor = start;
            while (cursor < end)
            {
                int windowEnd = Math.Min(cursor + MaxChunkSize, end);
                yield return (cursor, windowEnd);
                cursor = windowEnd;
            }
            yield break;
        }

        var sep = Separators[separatorIndex];
        int pieceStart = start;
        int searchFrom = start;
        while (true)
        {
            int idx = text.IndexOf(sep, searchFrom, end - searchFrom, StringComparison.Ordinal);
            if (idx < 0)
            {
                break;
            }

            int pieceEnd = idx + sep.Length;
            foreach (var split in Split(text, pieceStart, pieceEnd, separatorIndex + 1))
            {
                yield return split;
            }
            pieceStart = pieceEnd;
            searchFrom = pieceEnd;
        }
        if (pieceStart < end)
        {
            foreach (var split in Split(text, pieceStart, end, separatorIndex + 1))
            {
                yield return split;
            }
        }
    }
}
