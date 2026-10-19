using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Pigmentor.Core.Documents;

namespace Pigmentor.Ingestion.Indexing;

/// <summary>Embed the chunk itself; the baseline strategy.</summary>
public sealed class ChunkIndexingStrategy : IIndexingStrategy
{
    public string Strategy => "chunk";

    public async IAsyncEnumerable<IndexedItem> IndexAsync(
        DocumentChunk chunk,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        await Task.Yield();
        yield return new IndexedItem(chunk.Text, chunk);
    }
}

/// <summary>
/// Embed the first sentence of the chunk (the "proposition"); retrieve the
/// full chunk. Cheap and surprisingly effective on dense paragraph corpora.
/// </summary>
public sealed partial class SubChunkIndexingStrategy : IIndexingStrategy
{
    public string Strategy => "sub-chunk";

    [GeneratedRegex(@"(?<=[\.\!\?])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceSplit();

    public async IAsyncEnumerable<IndexedItem> IndexAsync(
        DocumentChunk chunk,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        await Task.Yield();
        var first = SentenceSplit().Split(chunk.Text, count: 2)[0].Trim();
        if (string.IsNullOrEmpty(first))
        {
            yield break;
        }
        yield return new IndexedItem(first, chunk);
    }
}

/// <summary>
/// Generate <c>QuestionsPerChunk</c> hypothetical questions per chunk via
/// an <see cref="IChatClient"/>; embed each question; retrieve the parent
/// chunk. Pays an extra LLM call per chunk at index time and produces the
/// strongest top-1 retrieval on FAQ-style corpora.
/// </summary>
public sealed class QueryIndexingStrategy : IIndexingStrategy
{
    private readonly IChatClient _chat;
    public int QuestionsPerChunk { get; }

    public QueryIndexingStrategy(IChatClient chat, int questionsPerChunk = 3)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(questionsPerChunk);
        _chat = chat;
        QuestionsPerChunk = questionsPerChunk;
    }

    public string Strategy => "query";

    public async IAsyncEnumerable<IndexedItem> IndexAsync(
        DocumentChunk chunk,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var prompt =
            $"Generate {QuestionsPerChunk} concise, distinct questions whose answers would be found " +
            $"in the following passage. Output one question per line, no numbering, no preamble.\n\n" +
            $"Passage:\n{chunk.Text}";
        var response = await _chat.GetResponseAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        var text = response.Text ?? string.Empty;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new IndexedItem(line, chunk);
        }
    }
}

/// <summary>Embed an LLM-generated summary; retrieve the original chunk.</summary>
public sealed class SummaryIndexingStrategy : IIndexingStrategy
{
    private readonly IChatClient _chat;

    public SummaryIndexingStrategy(IChatClient chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        _chat = chat;
    }

    public string Strategy => "summary";

    public async IAsyncEnumerable<IndexedItem> IndexAsync(
        DocumentChunk chunk,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var prompt = $"Summarize the following passage in one or two sentences.\n\nPassage:\n{chunk.Text}";
        var response = await _chat.GetResponseAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        var summary = (response.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(summary))
        {
            yield break;
        }
        yield return new IndexedItem(summary, chunk);
    }
}
