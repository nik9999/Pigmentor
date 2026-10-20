using Microsoft.Extensions.AI;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval.Vectorless;

/// <summary>
/// The <strong>topic-query fallback</strong> for structural retrieval. When a
/// question names no explicit identifier, this router asks an
/// <see cref="IChatClient"/> to pick the most relevant section(s) of the
/// document by title and path, then returns those sections' text.
/// <para>
/// This is <em>not</em> "vectorless retrieval" in the deterministic sense of
/// <see cref="StructuralRetriever"/> (O(1) id lookup, exact cross-reference
/// following). It is LLM routing: a non-deterministic model call that stands in
/// for the chapter's "topic lookup … a vector retrieval over node titles, a
/// keyword search, or a hand-curated mapping." Wire it in as the
/// <c>topicFallback</c> argument to <see cref="StructuralRetriever"/>.
/// </para>
/// </summary>
public sealed class LlmSectionRouter : IRetriever
{
    private readonly StructuralIndex _index;
    private readonly IChatClient _chat;

    /// <summary>Create the router over a structural index and a chat client.</summary>
    public LlmSectionRouter(StructuralIndex index, IChatClient chat)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(chat);
        _index = index;
        _chat = chat;
    }

    /// <inheritdoc />
    public string Strategy => "llm-section";

    /// <inheritdoc />
    public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default) =>
        RetrieveAsync(new RetrievalRequest(query, topK), cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Filter is not null)
        {
            throw new NotSupportedException($"Retriever '{Strategy}' does not support metadata filtering.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.TopK);

        var nodes = _index.AllNodes().ToList();
        var titles = string.Join("\n", nodes.Select((n, i) => $"{i}: {n.Path}"));

        var prompt = BuildPrompt(titles, request.Query, request.TopK);

        var response = await _chat.GetResponseAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        var raw = response.Text ?? string.Empty;
        var picks = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : -1)
            .Where(n => n >= 0 && n < nodes.Count)
            .Distinct()
            .Take(request.TopK)
            .ToList();

        var meta = new DocumentMetadata("structural", "structural", "Structural", "All",
            "Internal", "Section", 2026, "structural",
            new DateOnly(2026, 1, 1), _index.Root.Title);

        return [.. picks
            .Select((idx, rank) =>
            {
                var node = nodes[idx];
                var text = $"{node.Path}\n\n{node.Text}";
                var chunk = new DocumentChunk(node.Id, "structural", rank, text, 0, text.Length, meta);
                return new RetrievalResult(chunk, 1.0 / (rank + 1));
            })];
    }

    internal static string BuildPrompt(string titles, string query, int topK) =>
        $"Определи наиболее релевантные разделы структурированного документа для ответа на вопрос. " +
        $"Ответь списком целочисленных индексов через запятую (до {topK} шт.) в порядке убывания релевантности. " +
        $"Не добавляй пояснений и текста.\n\n" +
        $"Разделы:\n{titles}\n\n" +
        $"Вопрос: {query}";
}
