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

    internal const string Prompt =
        "Сформулируй {0} различных альтернативных вариантов следующего вопроса пользователя на русском языке. " +
        "Сохраняй исходные технические термины, названия протоколов, регистров, параметров, стандартов и моделей без перевода. " +
        "Выводи строго по одному варианту на строку, без нумерации, списков и вводных фраз.\n\nВопрос: {1}";

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

        var prompt = Prompt.Replace("{0}", QueryVariants.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
                           .Replace("{1}", request.Query, StringComparison.Ordinal);
        var response = await _chat.GetResponseAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        var variants = (response.Text ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(QueryVariants)
            .ToList();
        // Always include the original query.
        variants.Insert(0, request.Query);

        var candidateK = Math.Max(request.TopK * 2, request.TopK + 5);
        var lists = await Task.WhenAll(variants.Select(v => _inner.RetrieveAsync(request with { Query = v, TopK = candidateK }, cancellationToken))).ConfigureAwait(false);

        if (lists.Length == 0)
        {
            return Array.Empty<RetrievalResult>();
        }
        return _merger.Merge(lists, request.TopK);
    }
}
