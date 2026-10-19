using Microsoft.Extensions.AI;
using Pigmentor.Core.Abstractions;
using Pigmentor.Core.Documents;

namespace Pigmentor.Retrieval.Decorators;

/// <summary>
/// RAG-Fusion — generate <see cref="QueryVariants"/> alternative phrasings
/// of the query via an <see cref="IChatClient"/>, run the inner retriever
/// for each, and merge the result lists with RRF. Strong on broad /
/// open-ended queries.
/// </summary>
public sealed class RagFusionRetriever : IRetriever
{
    private readonly IRetriever _inner;
    private readonly IChatClient _chat;
    private readonly RrfMerger _merger;
    public int QueryVariants { get; }

    private const string Prompt =
        "Generate {0} distinct alternative phrasings of the following user question. " +
        "Output one per line, no numbering, no preamble.\n\nQuestion: {1}";

    public RagFusionRetriever(IRetriever inner, IChatClient chat, int queryVariants = 3, RrfMerger? merger = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queryVariants);
        _inner = inner;
        _chat = chat;
        QueryVariants = queryVariants;
        _merger = merger ?? new RrfMerger();
    }

    public string Strategy => $"rag-fusion({_inner.Strategy})";

    public async Task<IReadOnlyList<RetrievalResult>> RetrieveAsync(
        string query,
        int topK,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);

        var prompt = Prompt.Replace("{0}", QueryVariants.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                           .Replace("{1}", query, StringComparison.Ordinal);
        var response = await _chat.GetResponseAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        var variants = (response.Text ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(QueryVariants)
            .ToList();
        // Always include the original query.
        variants.Insert(0, query);

        var candidateK = Math.Max(topK * 2, topK + 5);
        var lists = await Task.WhenAll(variants.Select(v => _inner.RetrieveAsync(v, candidateK, cancellationToken))).ConfigureAwait(false);

        if (lists.Length == 0)
        {
            return Array.Empty<RetrievalResult>();
        }
        var merged = lists[0];
        for (int i = 1; i < lists.Length; i++)
        {
            merged = _merger.Merge(merged, lists[i], topK * 2);
        }
        return [.. merged.Take(topK)];
    }
}
