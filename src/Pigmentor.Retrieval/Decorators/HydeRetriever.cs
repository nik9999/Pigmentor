using Microsoft.Extensions.AI;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval.Decorators;

/// <summary>
/// HyDE (Hypothetical Document Embeddings) — generate a hypothetical
/// answer to the query first, then retrieve neighbors of that hypothetical
/// answer's text rather than the bare query. Helps when the query is short
/// or ambiguous and the corpus contains long-form passages.
/// </summary>
public sealed class HydeRetriever : IRetriever
{
    private readonly IRetriever _inner;
    private readonly IChatClient _chat;

    private const string Prompt =
        "Write a short, hypothetical 2-3 sentence answer to the following question. " +
        "It does NOT need to be factual — it just needs to look like the kind of passage " +
        "we expect to find in a knowledge base. Reply with only the passage, no preamble.\n\n" +
        "Question: {0}";

    public HydeRetriever(IRetriever inner, IChatClient chat)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(chat);
        _inner = inner;
        _chat = chat;
    }

    public string Strategy => $"hyde({_inner.Strategy})";

    public Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default) =>
        RetrieveAsync(new RetrievalRequest(query, topK), cancellationToken);

    public async Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        RetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.TopK);

        var prompt = Prompt.Replace("{0}", request.Query, StringComparison.Ordinal);
        var response = await _chat.GetResponseAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        var hypothetical = (response.Text ?? string.Empty).Trim();
        var searchText = string.IsNullOrEmpty(hypothetical) ? request.Query : hypothetical;
        return await _inner.RetrieveAsync(request with { Query = searchText }, cancellationToken).ConfigureAwait(false);
    }
}
