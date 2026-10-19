using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Tokens;

namespace Pigmentor.Ingestion.Chunking;

/// <summary>
/// Decorates any inner <see cref="IChunker"/> and enforces a <em>token</em>
/// budget (not a character budget) using an <see cref="ITokenCounter"/>. The
/// repo's other chunkers all measure characters; this decorator is the bridge
/// from a model-facing intent ("512-token chunks") to chunks that actually fit
/// that budget under the model's tokenizer.
/// </summary>
/// <remarks>
/// <para>Each chunk the inner chunker emits is checked against
/// <see cref="MaxTokens"/>. Chunks already within budget pass through unchanged
/// (offsets and IDs preserved). A chunk over budget is sub-split at word
/// boundaries into as many pieces as needed, greedily packing words until the
/// next word would breach the budget. The sub-pieces are re-indexed so the
/// emitted stream keeps a contiguous <see cref="DocumentChunk.ChunkIndex"/>
/// sequence; their char offsets are mapped back onto the original chunk's span
/// so citations still resolve.</para>
/// <para>The decorator is deliberately conservative: it never merges across the
/// inner chunker's boundaries, only splits within them, so it composes with any
/// upstream strategy without changing that strategy's grouping decisions.</para>
/// </remarks>
public sealed class TokenAwareChunker : IChunker
{
    private readonly IChunker _inner;
    private readonly ITokenCounter _tokenCounter;

    /// <summary>The maximum number of tokens any emitted chunk may contain.</summary>
    public int MaxTokens { get; }

    /// <summary>Create a token-budget decorator around <paramref name="inner"/>.</summary>
    /// <param name="inner">The chunker whose output is re-checked against the token budget.</param>
    /// <param name="tokenCounter">Counts tokens under the target model's encoding.</param>
    /// <param name="maxTokens">The per-chunk token ceiling (for example 512).</param>
    public TokenAwareChunker(IChunker inner, ITokenCounter tokenCounter, int maxTokens = 512)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(tokenCounter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTokens);
        _inner = inner;
        _tokenCounter = tokenCounter;
        MaxTokens = maxTokens;
    }

    public string Strategy => $"token-aware({_inner.Strategy})";

    public async IAsyncEnumerable<DocumentChunk> ChunkAsync(
        Document document,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        int emittedIndex = 0;
        await foreach (var chunk in _inner.ChunkAsync(document, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_tokenCounter.CountTokens(chunk.Text) <= MaxTokens)
            {
                yield return chunk with { ChunkIndex = emittedIndex, ChunkId = $"{chunk.DocumentId}#{emittedIndex}" };
                emittedIndex++;
                continue;
            }

            foreach (var piece in SplitToBudget(chunk))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return piece with { ChunkIndex = emittedIndex, ChunkId = $"{chunk.DocumentId}#{emittedIndex}" };
                emittedIndex++;
            }
        }
    }

    /// <summary>
    /// Greedily packs whitespace-delimited words from <paramref name="chunk"/>
    /// into sub-chunks that each stay within <see cref="MaxTokens"/>. A single
    /// word that exceeds the budget on its own is emitted as its own (oversized)
    /// piece rather than dropped, so no content is lost.
    /// </summary>
    private IEnumerable<DocumentChunk> SplitToBudget(DocumentChunk chunk)
    {
        // Split on whitespace runs while remembering each word's char span in the
        // parent text, so we can map sub-chunk offsets back onto the source.
        var words = Tokenize(chunk.Text);
        if (words.Count == 0)
        {
            yield break;
        }

        int pieceStart = words[0].Start;
        int pieceEnd = words[0].End;
        bool pieceHasWord = false;

        foreach (var word in words)
        {
            int candidateEnd = word.End;
            var candidate = chunk.Text[pieceStart..candidateEnd];
            if (pieceHasWord && _tokenCounter.CountTokens(candidate) > MaxTokens)
            {
                // Adding this word would breach the budget — flush the packed
                // words and start a new piece at the current word.
                yield return Slice(chunk, pieceStart, pieceEnd);
                pieceStart = word.Start;
                pieceHasWord = false;
            }

            pieceEnd = candidateEnd;
            pieceHasWord = true;
        }

        if (pieceHasWord)
        {
            yield return Slice(chunk, pieceStart, pieceEnd);
        }
    }

    private static List<(int Start, int End)> Tokenize(string text)
    {
        var words = new List<(int Start, int End)>();
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i > start)
            {
                words.Add((start, i));
            }
        }

        return words;
    }

    /// <summary>
    /// Carves a sub-chunk out of <paramref name="parent"/>'s text in the range
    /// [<paramref name="localStart"/>, <paramref name="localEnd"/>), mapping the
    /// local offsets back onto the parent's source span so citations resolve.
    /// </summary>
    private static DocumentChunk Slice(DocumentChunk parent, int localStart, int localEnd)
    {
        var sliced = parent.Text[localStart..localEnd].Trim();
        int parentSpan = parent.EndCharOffset - parent.StartCharOffset;
        int mappedStart = parent.StartCharOffset + Math.Min(localStart, Math.Max(parentSpan, 0));
        int mappedEnd = parent.StartCharOffset + Math.Min(localEnd, Math.Max(parentSpan, 0));
        return parent with
        {
            Text = sliced,
            StartCharOffset = mappedStart,
            EndCharOffset = mappedEnd,
        };
    }
}
