using System.Text;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Tokens;
using Pigmentor.Retrieval;

namespace Pigmentor.Generation;

/// <summary>
/// Default implementation of <see cref="IContextBuilder"/> that respects token budgets,
/// applies lost-in-the-middle interleaving, and generates stable [S1], [S2] source identifiers.
/// </summary>
public sealed class ContextBuilder : IContextBuilder
{
    private readonly ITokenCounter _tokenCounter;
    private readonly ContextBuilderOptions _options;

    public ContextBuilder(
        ITokenCounter tokenCounter,
        ContextBuilderOptions? options = null)
    {
        _tokenCounter = tokenCounter ?? throw new ArgumentNullException(nameof(tokenCounter));
        _options = options ?? new ContextBuilderOptions();
    }

    /// <inheritdoc />
    public RagContext BuildContext(
        IReadOnlyList<RetrievalResult> candidates,
        int maxTokens) => maxTokens <= 0 || candidates.Count == 0
            ? RagContext.Empty
            : BuildContext(candidates, new ContextBudget(maxTokens, 0));

    /// <inheritdoc />
    public RagContext BuildContext(
        IReadOnlyList<RetrievalResult> candidates,
        ContextBudget budget)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var availableTokens = budget.AvailableTokens;
        if (candidates.Count == 0 || availableTokens <= 0)
        {
            return RagContext.Empty;
        }

        // 1. Candidate selection under token budget (best-first in candidate relevance order)
        var selected = new List<RetrievalResult>();
        var currentTokenCount = 0;

        foreach (var candidate in candidates)
        {
            var nextSourceId = $"S{selected.Count + 1}";
            var separatorTokens = selected.Count > 0 ? _tokenCounter.CountTokens("\n\n") : 0;
            var candidateBlock = FormatSource(nextSourceId, candidate.Chunk);
            var blockTokens = _tokenCounter.CountTokens(candidateBlock);

            if (currentTokenCount + separatorTokens + blockTokens <= availableTokens)
            {
                selected.Add(candidate);
                currentTokenCount += separatorTokens + blockTokens;
            }
            else if (_options.OversizedBehavior == OversizedChunkBehavior.Truncate)
            {
                var maxBlockTokens = availableTokens - currentTokenCount - separatorTokens;
                var headerTokens = _tokenCounter.CountTokens(FormatSourceHeader(nextSourceId, candidate.Chunk));
                if (maxBlockTokens > headerTokens + 5) // ensure at least a few tokens for body
                {
                    var truncatedChunk = TruncateChunk(candidate.Chunk, nextSourceId, maxBlockTokens);
                    if (!string.IsNullOrEmpty(truncatedChunk.Text))
                    {
                        selected.Add(new RetrievalResult(truncatedChunk, candidate.Score));
                    }
                }
                break;
            }
            else
            {
                // OversizedChunkBehavior.Drop: skip oversized chunk without truncation (§6)
                continue;
            }
        }

        if (selected.Count == 0)
        {
            return RagContext.Empty;
        }

        // 2. Ordering: apply ContextOrdering.Interleave if enabled and multiple sources
        var orderedList = (_options.EnableInterleaving && selected.Count > 1
            ? ContextOrdering.Interleave(selected)
            : selected).ToList();

        // 3. Source formatting with stable [S1], [S2] tags and SourceMap population
        var (formattedContext, tokenCount, sourceMap) = FormatContext(orderedList);

        // 4. Strict budget invariant guard: tokenCount must never exceed availableTokens (§5)
        while (tokenCount > availableTokens && orderedList.Count > 0)
        {
            orderedList.RemoveAt(orderedList.Count - 1);
            if (orderedList.Count == 0)
            {
                return RagContext.Empty;
            }
            (formattedContext, tokenCount, sourceMap) = FormatContext(orderedList);
        }

        return new RagContext(formattedContext, orderedList, tokenCount, sourceMap);
    }

    private (string FormattedContext, int TokenCount, Dictionary<string, RetrievalResult> SourceMap) FormatContext(
        List<RetrievalResult> sources)
    {
        var sourceMap = new Dictionary<string, RetrievalResult>(sources.Count, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();

        for (var i = 0; i < sources.Count; i++)
        {
            var sourceId = $"S{i + 1}";
            sourceMap[sourceId] = sources[i];

            if (sb.Length > 0)
            {
                sb.Append("\n\n");
            }

            sb.Append(FormatSource(sourceId, sources[i].Chunk));
        }

        var formatted = sb.ToString();
        var tokens = _tokenCounter.CountTokens(formatted);
        return (formatted, tokens, sourceMap);
    }

    private static string FormatSource(string sourceId, DocumentChunk chunk)
    {
        var header = FormatSourceHeader(sourceId, chunk);
        return $"{header}\n{chunk.Text}";
    }

    private static string FormatSourceHeader(string sourceId, DocumentChunk chunk)
    {
        var title = !string.IsNullOrWhiteSpace(chunk.Metadata?.Title)
            ? chunk.Metadata.Title
            : chunk.DocumentId;

        return $"[{sourceId}] Title: {title} | Document: {chunk.DocumentId}";
    }

    private DocumentChunk TruncateChunk(DocumentChunk chunk, string sourceId, int maxBlockTokens)
    {
        var text = chunk.Text;
        if (string.IsNullOrEmpty(text))
        {
            return chunk;
        }

        var low = 0;
        var high = text.Length;
        var bestLength = 0;

        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            var candidateText = text[..mid] + "...";
            var candidateChunk = chunk with
            {
                Text = candidateText,
                EndCharOffset = chunk.StartCharOffset + mid
            };
            var candidateBlock = FormatSource(sourceId, candidateChunk);
            var tokens = _tokenCounter.CountTokens(candidateBlock);

            if (tokens <= maxBlockTokens)
            {
                bestLength = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        var truncatedText = bestLength > 0 ? text[..bestLength] + "..." : string.Empty;
        return new DocumentChunk(
            ChunkId: chunk.ChunkId,
            DocumentId: chunk.DocumentId,
            ChunkIndex: chunk.ChunkIndex,
            Text: truncatedText,
            StartCharOffset: chunk.StartCharOffset,
            EndCharOffset: chunk.StartCharOffset + bestLength,
            Metadata: chunk.Metadata)
        {
            Provenance = chunk.Provenance
        };
    }
}
